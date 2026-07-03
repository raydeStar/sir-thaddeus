using System.Globalization;
using System.Text.RegularExpressions;
using SirThaddeus.Agent.Reasoning;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// For strict-answer reasoning items (a bare number or A–D letter), samples the
/// model several times with a step-by-step prompt and returns the majority-vote
/// final answer. This is the honest lever for the variance the calculator can't
/// fix: a single sample of a multi-step problem is unreliable (the same
/// recurrence scored 32 once and 37 once), but the majority across N samples is
/// stable. The model does all the reasoning; only the vote is mechanical.
///
/// <para>Opt-in and off by default: enabled only when <c>ST_SELF_CONSISTENCY</c>
/// is set to N&gt;=2 (capped at 9). When disabled, or when the prompt isn't a
/// strict numeric/choice item, the step is a no-op and the normal pipeline runs.</para>
/// </summary>
public sealed class SelfConsistencyStep : ITurnStep
{
    private static readonly Regex NumericOnlyPrompt = new(
        @"reply\s+with\s+only\b[^.]*\b(integer|number|decimal|value|remainder|count|sum|digits?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ChoiceOnlyPrompt = new(
        @"reply\s+with\s+only\s+a\s*,?\s*b\s*,?\s*c\s*,?\s*(?:or\s+)?d\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Real benchmark fixtures (MMLU-Pro, AIME) don't say "reply with only X";
    // they say "put the final answer on its own line". Fire on that too, and
    // tell choice from numeric by whether the prompt lists A–J options.
    private static readonly Regex FinalAnswerInstruction = new(
        @"final\s+answer",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ChoiceOptionLine = new(
        @"(?m)^\s*\(?[A-J][.)]\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ILlmClient _llm;
    private readonly int _samples;
    private readonly double _samplingTemperature;
    private readonly double _minAgreement;

    public SelfConsistencyStep(
        ILlmClient llm,
        int? samples = null,
        double? samplingTemperature = null,
        double? minAgreement = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
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

        var hasFinalAnswer = FinalAnswerInstruction.IsMatch(context.UserText);
        var isChoice = ChoiceOnlyPrompt.IsMatch(context.UserText)
            || ChoiceOptionLine.Matches(context.UserText).Count >= 3;
        var isNumeric = !isChoice
            && (NumericOnlyPrompt.IsMatch(context.UserText) || hasFinalAnswer);
        if (!isChoice && !isNumeric)
            return new StepResult.Continue(context);

        Func<string?, string?> extract = isChoice
            ? SelfConsistency.ExtractChoice
            : SelfConsistency.ExtractNumeric;

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

        // Strict-answer contract: emit just the winning value.
        return new StepResult.Terminate(new AgentResponse
        {
            Text = vote.Answer,
            Success = true,
        });
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
    // benchmark config runs the model at temperature 0. Override with
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
