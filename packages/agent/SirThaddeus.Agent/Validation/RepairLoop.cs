using System.Diagnostics;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Validation;

/// <summary>
/// Executes exactly <see cref="MaxAttempts"/> targeted repair attempts when
/// the <see cref="CompletionValidator"/> flags a response as inadequate.
/// Uses the validator's <c>MissingElement</c> and <c>SuggestedRepair</c> to
/// build a focused repair prompt — never a full re-run.
/// </summary>
public sealed class RepairLoop
{
    private readonly ILlmClient _llm;
    private readonly CompletionValidator _validator;
    private const int RepairMaxTokens = 512;

    /// <summary>
    /// Maximum repair attempts per failed validation. Default 1.
    /// </summary>
    public int MaxAttempts { get; set; } = 1;

    public RepairLoop(ILlmClient llm, CompletionValidator validator)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>
    /// Attempts to repair a response that failed validation.
    /// Returns a <see cref="RepairResult"/> with the final text and attempt log.
    /// </summary>
    public async Task<RepairResult> TryRepairAsync(
        string userRequest,
        string failedResponse,
        CompletionValidationResult failedValidation,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken = default)
    {
        var attempts = new List<RepairAttempt>();
        var currentText = failedResponse;
        var currentValidation = failedValidation;

        for (var i = 1; i <= MaxAttempts; i++)
        {
            var sw = Stopwatch.StartNew();

            var repairPrompt = BuildRepairPrompt(
                userRequest, currentText, currentValidation);

            try
            {
                var messages = new List<ChatMessage>
                {
                    ChatMessage.System(RepairSystemPrompt),
                    ChatMessage.User(repairPrompt)
                };

                var llmResponse = await _llm.ChatAsync(
                    messages, tools: null, RepairMaxTokens, cancellationToken);

                sw.Stop();
                var repairedText = llmResponse.Content?.Trim();

                if (string.IsNullOrWhiteSpace(repairedText))
                {
                    attempts.Add(new RepairAttempt
                    {
                        AttemptNumber = i,
                        FailureReason = currentValidation.MissingElement ?? "unknown",
                        RepairPrompt = repairPrompt,
                        RepairedText = null,
                        RepairSucceeded = false,
                        ElapsedMs = sw.Elapsed.TotalMilliseconds
                    });
                    break;
                }

                // Re-validate the repaired response.
                var hasToolResults = toolCallsMade.Count > 0 &&
                                     toolCallsMade.Any(t => t.Success);
                var revalidation = await _validator.ValidateAsync(
                    userRequest, repairedText, hasToolResults, cancellationToken);

                var succeeded = revalidation.Passed;
                attempts.Add(new RepairAttempt
                {
                    AttemptNumber = i,
                    FailureReason = currentValidation.MissingElement ?? "unknown",
                    RepairPrompt = repairPrompt,
                    RepairedText = repairedText,
                    RepairSucceeded = succeeded,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds
                });

                if (succeeded)
                {
                    return new RepairResult
                    {
                        Repaired = true,
                        FinalText = repairedText,
                        Attempts = attempts
                    };
                }

                // Next iteration will use the latest text and validation.
                currentText = repairedText;
                currentValidation = revalidation;
            }
            catch
            {
                sw.Stop();
                attempts.Add(new RepairAttempt
                {
                    AttemptNumber = i,
                    FailureReason = currentValidation.MissingElement ?? "unknown",
                    RepairPrompt = repairPrompt,
                    RepairedText = null,
                    RepairSucceeded = false,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds
                });
                break;
            }
        }

        // All attempts exhausted or failed.
        return new RepairResult
        {
            Repaired = false,
            FinalText = failedResponse,
            Attempts = attempts
        };
    }

    internal static string BuildRepairPrompt(
        string userRequest,
        string failedResponse,
        CompletionValidationResult validation)
    {
        var issue = validation.MissingElement ?? "The response did not adequately answer the question.";
        var suggestion = validation.SuggestedRepair ?? "Address the specific issue noted above.";

        return $"""
            The user asked:
            {userRequest}

            Your previous response:
            {failedResponse}

            Specific issue: {issue}
            Suggested fix: {suggestion}

            Fix only this specific issue. Do not rewrite the entire response. Keep the parts that were correct.
            """;
    }

    private const string RepairSystemPrompt =
        "You are correcting a specific flaw in an earlier answer. " +
        "Fix only the identified issue. Keep everything else intact. " +
        "Do not add disclaimers about the correction. " +
        "Output only the corrected response text.";
}

/// <summary>
/// Outcome of a repair loop run.
/// </summary>
public sealed record RepairResult
{
    /// <summary>True if at least one repair attempt passed validation.</summary>
    public required bool Repaired { get; init; }

    /// <summary>
    /// The final response text to show the user.
    /// If <see cref="Repaired"/> is true, this is the repaired version.
    /// If false, this is the original failed response.
    /// </summary>
    public required string FinalText { get; init; }

    /// <summary>Log of all repair attempts made.</summary>
    public IReadOnlyList<RepairAttempt> Attempts { get; init; } = [];
}
