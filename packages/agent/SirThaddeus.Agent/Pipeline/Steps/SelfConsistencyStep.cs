using System.Globalization;
using System.Text.RegularExpressions;
using SirThaddeus.Agent.Reasoning;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// For strict-answer reasoning items (a bare number or A–D letter), samples the
/// model several times with a step-by-step prompt and returns the majority-vote
/// final answer. This is the honest lever for the variance the calculator can't
/// fix: a single sample of a multi-step problem can be unreliable, while the
/// majority across independent samples is often more stable. The model does all
/// the reasoning; only the vote is mechanical.
///
/// <para>Opt-in and off by default: enabled only when <c>ST_SELF_CONSISTENCY</c>
/// is set to N&gt;=2 (capped at 9). When disabled, or when the prompt isn't a
/// strict numeric/choice item, the step is a no-op and the normal pipeline runs.</para>
///
/// <para><b>Tool-aware mode</b> (opt-in via <c>ST_SELF_CONSISTENCY_TOOLS=1</c>,
/// requires N&gt;=2, a wired <c>toolLoop</c> collaborator, and a non-empty
/// tool list): instead of sampling plain chain-of-thought with tools disabled,
/// it runs the FULL tool loop N times and majority-votes the *computed* answers.
/// This is the lever for compute-bound problems (Collatz, prime counting) where
/// the answer must come from python_eval/calculator — plain CoT can't reach them.
/// When the flag is unset the step behaves exactly as the CoT-only path above.</para>
/// </summary>
public sealed class SelfConsistencyStep : ITurnStep
{
    private static readonly Regex ChoiceOnlyPrompt = new(
        @"reply\s+with\s+only\s+a\s*,?\s*b\s*,?\s*c\s*,?\s*(?:or\s+)?d\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Some task formats request a labeled final-answer line rather than a bare
    // value. Treat that as strict output too, distinguishing choice prompts by
    // whether they provide A–J option lines.
    private static readonly Regex FinalAnswerInstruction = new(
        @"final\s+answer",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ChoiceOptionLine = new(
        @"(?m)^\s*\(?[A-J][.)]\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ILlmClient _llm;
    private readonly ITurnStep? _toolLoop;
    private readonly int _samples;
    private readonly double _samplingTemperature;
    private readonly double _minAgreement;

    public SelfConsistencyStep(
        ILlmClient llm,
        int? samples = null,
        double? samplingTemperature = null,
        double? minAgreement = null,
        ITurnStep? toolLoop = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _toolLoop = toolLoop;
        _samples = samples ?? ReadConfiguredSampleCount();
        _samplingTemperature = samplingTemperature ?? ReadConfiguredTemperature();
        _minAgreement = minAgreement ?? ReadConfiguredMinAgreement();
    }

    public string Name => "SelfConsistency";

    public async Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_samples < 2 || string.IsNullOrWhiteSpace(context.UserText))
            return new StepResult.Continue(context);

        if (!TryClassifyStrictPrompt(context.UserText, out var isChoice))
            return new StepResult.Continue(context);

        Func<string?, string?> extract = isChoice
            ? SelfConsistency.ExtractChoice
            : SelfConsistency.ExtractNumeric;

        // Tool-aware mode (opt-in via ST_SELF_CONSISTENCY_TOOLS): run the FULL
        // tool loop N times and majority-vote the *computed* answers. This is
        // the only path that can help compute-bound problems (Collatz, prime
        // counting) where the answer comes from python_eval/calculator, not
        // from plain chain-of-thought. Everything else stays exactly as before.
        if (ToolAwareModeEnabled() &&
            _toolLoop is not null &&
            context.ToolDefs.Count > 0)
        {
            return await RunToolAwareAsync(context, extract, isChoice, cancellationToken).ConfigureAwait(false);
        }

        var messages = BuildChainOfThoughtMessages(context);

        var samples = new List<string>(_samples);
        for (var i = 0; i < _samples; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await _llm
                    .ChatAsync(messages, tools: null, maxTokensOverride: 512, temperatureOverride: _samplingTemperature, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(response.Content))
                    samples.Add(response.Content);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A failed sample just doesn't vote; fall back gracefully.
            }

            // Adaptive early-stop: once the leader's margin exceeds the samples
            // still to come, the majority can't change — stop and save the rest.
            if (SelfConsistency.MajorityLocked(samples, extract, _samples))
                break;
        }

        return BuildVoteResult(context, samples, extract, toolCallsMade: null);
    }

    /// <summary>
    /// Tool-aware self-consistency: run the full tool loop up to N times,
    /// extract each run's strict answer from its <see cref="TurnContext.AssistantDraft"/>
    /// (or the response text if the loop terminated), and majority-vote. Each
    /// run gets its own fresh message list and empty tool-call log so runs can't
    /// leak state into each other (ToolLoopStep appends to the list it's given).
    ///
    /// <para>A single failed run is log-swallowed so it can't kill the turn; if
    /// every run fails or produces no answer, we fall through to
    /// <see cref="StepResult.Continue"/> and the normal pipeline runs once.</para>
    ///
    /// <para>Temperature: we intentionally do NOT plumb a sampling temperature
    /// into the tool loop in this pass — the LM Studio MoE is nondeterministic
    /// even at temperature 0, which supplies the sample diversity the vote needs.
    /// A per-run temperature seam through ToolLoopStep is a possible follow-up.</para>
    /// </summary>
    private async Task<StepResult> RunToolAwareAsync(
        TurnContext context,
        Func<string?, string?> extract,
        bool isChoice,
        CancellationToken cancellationToken)
    {
        var samples = new List<string>(_samples);
        var mergedToolCalls = new List<ToolCallRecord>();

        for (var i = 0; i < _samples; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Each run MUST get its own message list — ToolLoopStep appends
                // to the list it's handed, so a shared instance would let one
                // run's tool round-trips leak into the next. ChatMessage is an
                // immutable record, so a shallow copy of the list is safe. A
                // fresh empty ToolCallsMade keeps each run's audit isolated.
                var runContext = context with
                {
                    LlmMessages = new List<ChatMessage>(context.LlmMessages),
                    ToolCallsMade = new List<ToolCallRecord>(),
                    AssistantDraft = null,
                };

                var result = await _toolLoop!.ExecuteAsync(runContext, cancellationToken).ConfigureAwait(false);

                string? runAnswer = null;
                switch (result)
                {
                    case StepResult.Continue cont:
                        runAnswer = cont.Next.AssistantDraft;
                        mergedToolCalls.AddRange(cont.Next.ToolCallsMade);
                        break;
                    case StepResult.Terminate term:
                        // The tool loop finished the turn itself (e.g. a
                        // deterministic draft or the round-trip cap). Treat its
                        // response text as this run's answer and keep its audit.
                        runAnswer = term.Response.Text;
                        mergedToolCalls.AddRange(term.Response.ToolCallsMade);
                        break;
                }

                // GROUNDING RULE: a run may only vote when its answer is a bare
                // value — a bare number means the strict-compute draft adopted
                // the run's own successful tool output (or the model emitted the
                // contract shape); a bare letter is a well-formed choice. A
                // verbose, ungrounded draft must ABSTAIN: measured live, a run
                // whose python calls all failed produced prose from which the
                // numeric extractor harvested the QUESTION's own trailing number
                // ("...primes below 100" -> "100"), and three such artifacts
                // out-voted five clean baseline passes. If every run abstains we
                // fall through to Continue, so tool-aware SC degrades to the
                // normal single-run pipeline instead of ever doing worse.
                if (IsGroundedVote(runAnswer, isChoice))
                    samples.Add(runAnswer!.Trim());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A single failed run just doesn't vote — it must not kill the
                // turn. If every run fails we fall through to Continue below.
            }

            // Adaptive early-stop: once the leader can no longer be caught, stop.
            if (SelfConsistency.MajorityLocked(samples, extract, _samples))
                break;
        }

        return BuildVoteResult(context, samples, extract, mergedToolCalls);
    }

    /// <summary>Shared vote + terminate tail for both the CoT and tool-aware
    /// paths. Falls through to <see cref="StepResult.Continue"/> when no sample
    /// produced a parseable answer or the optional consensus gate isn't met, so
    /// the normal pipeline runs once instead of the turn being overridden.</summary>
    private StepResult BuildVoteResult(
        TurnContext context,
        IReadOnlyList<string> samples,
        Func<string?, string?> extract,
        IReadOnlyList<ToolCallRecord>? toolCallsMade)
    {
        if (samples.Count == 0)
            return new StepResult.Continue(context);

        var vote = SelfConsistency.Vote(samples, extract);
        if (string.IsNullOrWhiteSpace(vote.Answer))
            return new StepResult.Continue(context);

        // Consensus gate (opt-in): when a positive threshold is configured and
        // the winner's agreement falls short, fall through to the normal
        // pipeline instead of overriding it. Measured on solver-probe the gate
        // helps neither model as a default — the 1.2B lost a marginal-but-
        // correct plurality win, and the 8B's bad samples agree with each
        // other, sailing past any threshold — so it is OFF unless configured.
        // SC itself is already per-model opt-in, which is the honest lever for
        // models whose greedy answer is reliable.
        if (_minAgreement > 0 && !SelfConsistency.HasStrongConsensus(vote, _minAgreement))
            return new StepResult.Continue(context);

        // Strict-answer contract: emit just the winning value. Carry the merged
        // tool calls (tool-aware path) so the response stays truthful about the
        // work the model actually did — the harness reconstructs the trace from
        // the audit log, but the response should not lie by omission.
        //
        // FromConsensusVote is set ONLY here — this is the one place a real
        // majority vote produces the turn's answer. The workflow coordinator
        // reads it to skip its confidence-gated retry: this answer was already
        // voted from N independent samples, so re-running the whole vote with a
        // "please re-check" preamble is redundant work on the turn that already
        // spent the most compute.
        return new StepResult.Terminate(new AgentResponse
        {
            Text = vote.Answer,
            Success = true,
            FromConsensusVote = true,
            ToolCallsMade = toolCallsMade is { Count: > 0 }
                ? toolCallsMade
                : Array.Empty<ToolCallRecord>(),
        });
    }

    /// <summary>Strict-answer detection shared by both paths: fires on bare
    /// numeric/choice requests and on explicit "final answer" instructions,
    /// telling choice from numeric by whether the
    /// prompt lists A–J option lines. Returns false for non-strict prompts.</summary>
    private static bool TryClassifyStrictPrompt(string userText, out bool isChoice)
    {
        var hasFinalAnswer = FinalAnswerInstruction.IsMatch(userText);
        isChoice = ChoiceOnlyPrompt.IsMatch(userText)
            || ChoiceOptionLine.Matches(userText).Count >= 3;
        var isNumeric = !isChoice
            && (StrictAnswerContract.RequestsBareNumeric(userText) || hasFinalAnswer);
        return isChoice || isNumeric;
    }

    /// <summary>A vote is grounded when the run's answer is already the bare
    /// contract shape: a bare number (the strict-compute draft adopted the
    /// run's own successful tool output) or a bare A–J letter for choice
    /// items. Anything verbose abstains — free-text extraction can harvest
    /// numbers from the question itself, and unanimous artifacts beat honest
    /// answers.</summary>
    private static bool IsGroundedVote(string? runAnswer, bool isChoice)
    {
        if (string.IsNullOrWhiteSpace(runAnswer))
            return false;

        var trimmed = runAnswer.Trim();
        return isChoice
            ? Regex.IsMatch(trimmed, "^[A-J]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : StrictAnswerContract.IsBareNumeric(trimmed);
    }

    // Tool-aware self-consistency is a distinct, more expensive opt-in on top of
    // plain SC: it runs the full tool loop per sample. Gate it behind its own
    // env flag so ST_SELF_CONSISTENCY alone keeps its exact current (CoT-only)
    // behavior. Accepts "1" or "true" (case-insensitive).
    private static bool ToolAwareModeEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("ST_SELF_CONSISTENCY_TOOLS");
        return string.Equals(raw, "1", StringComparison.Ordinal) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static List<ChatMessage> BuildChainOfThoughtMessages(TurnContext context)
    {
        var messages = context.LlmMessages.ToList();
        var cot = context.UserText
            + "\n\nWork through this step by step, showing each arithmetic step. "
            + "On the last line, write exactly: Final answer: <answer>";

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, "user", StringComparison.Ordinal))
            {
                messages[i] = ChatMessage.User(cot);
                return messages;
            }
        }

        messages.Add(ChatMessage.User(cot));
        return messages;
    }

    private static int ReadConfiguredSampleCount()
    {
        var raw = Environment.GetEnvironmentVariable("ST_SELF_CONSISTENCY");
        return int.TryParse(raw, out var n) && n > 1 ? Math.Min(n, 9) : 1;
    }

    // Sampling temperature for self-consistency. Diversity across samples is
    // what makes the majority vote meaningful, so this defaults to 0.9 (higher
    // than the usual 0.7) and is applied per-call, independent of the global
    // temperature — so SC never degenerates to identical samples even if a
    // deterministic configurations run the model at temperature 0. Override with
    // ST_SELF_CONSISTENCY_TEMP; clamped to a sane (0, 2] range.
    private static double ReadConfiguredTemperature()
    {
        var raw = Environment.GetEnvironmentVariable("ST_SELF_CONSISTENCY_TEMP");
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
            && t is > 0 and <= 2.0
            ? t
            : 0.9;
    }

    // Optional agreement threshold before sampled reasoning may short-circuit
    // the normal deterministic path (ST_SELF_CONSISTENCY_MIN_AGREEMENT,
    // 0.5–1.0). Default 0 = gate off: plurality wins, which measured best for
    // the small-model deployments SC is designed for.
    private static double ReadConfiguredMinAgreement()
    {
        var raw = Environment.GetEnvironmentVariable("ST_SELF_CONSISTENCY_MIN_AGREEMENT");
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value is >= 0.5 and <= 1.0
            ? value
            : 0;
    }
}
