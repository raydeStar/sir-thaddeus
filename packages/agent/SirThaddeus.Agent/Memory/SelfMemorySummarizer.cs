using System.Text;
using System.Text.Json;

namespace SirThaddeus.Agent.Memory;

/// <summary>
/// Deterministic self-memory summary/context rendering.
/// </summary>
public sealed class SelfMemorySummarizer : ISelfMemorySummarizer
{
    private const string MemoryListFactsToolName = "memory_list_facts";
    private const string MemoryListFactsToolNameAlt = "MemoryListFacts";

    private readonly IMcpToolClient _mcp;

    public SelfMemorySummarizer(IMcpToolClient mcp)
    {
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
    }

    public async Task<AgentResponse> BuildSummaryResponseAsync(
        string? activeProfileId,
        IList<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCallsMade);

        var (facts, error) = await LoadSelfScopedFactsAsync(activeProfileId, toolCallsMade, cancellationToken);
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new AgentResponse
            {
                Text = $"I couldn't read your stored facts right now ({error}).",
                Success = true,
                ToolCallsMade = toolCallsMade.ToList(),
                LlmRoundTrips = roundTrips
            };
        }

        return new AgentResponse
        {
            Text = BuildSelfMemorySummaryText(facts),
            Success = true,
            ToolCallsMade = toolCallsMade.ToList(),
            LlmRoundTrips = roundTrips
        };
    }

    public async Task<string> BuildContextBlockAsync(
        string? activeProfileId,
        IList<ToolCallRecord> toolCallsMade,
        Action<string, string>? logEvent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCallsMade);

        var (facts, error) = await LoadSelfScopedFactsAsync(activeProfileId, toolCallsMade, cancellationToken);
        if (!string.IsNullOrWhiteSpace(error))
        {
            logEvent?.Invoke("SELF_MEMORY_CONTEXT_SKIP", error);
            return "";
        }

        return BuildSelfMemoryContextBlock(facts);
    }

    public bool IsSelfMemoryKnowledgeRequest(string message)
    {
        var lower = (message ?? "").ToLowerInvariant().Trim();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var hasAboutMe = lower.Contains("about me", StringComparison.Ordinal) ||
                         lower.Contains("know me", StringComparison.Ordinal) ||
                         lower.Contains("remember me", StringComparison.Ordinal);
        if (!hasAboutMe)
            return false;

        return lower.Contains("what do you know", StringComparison.Ordinal) ||
               lower.Contains("what kind of things", StringComparison.Ordinal) ||
               lower.Contains("what do you remember", StringComparison.Ordinal) ||
               lower.Contains("what have you learned", StringComparison.Ordinal) ||
               lower.Contains("what information", StringComparison.Ordinal) ||
               lower.Contains("what info", StringComparison.Ordinal);
    }

    public bool IsPersonalizedUsingKnownSelfContextRequest(
        string message,
        bool hasLoadedProfileContext)
    {
        var lower = (message ?? "").ToLowerInvariant().Trim();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var hasAdviceCue =
            lower.Contains("recommend", StringComparison.Ordinal) ||
            lower.Contains("suggest", StringComparison.Ordinal) ||
            lower.Contains("plan", StringComparison.Ordinal) ||
            lower.Contains("best", StringComparison.Ordinal) ||
            lower.Contains("perfect", StringComparison.Ordinal) ||
            lower.Contains("help me choose", StringComparison.Ordinal) ||
            lower.Contains("what should i", StringComparison.Ordinal) ||
            lower.Contains("should i", StringComparison.Ordinal) ||
            lower.Contains("tailor", StringComparison.Ordinal) ||
            lower.Contains("personalized", StringComparison.Ordinal);

        if (!hasAdviceCue)
            return false;

        var referencesKnownSelf =
            lower.Contains("what you know about me", StringComparison.Ordinal) ||
            lower.Contains("based on what you know about me", StringComparison.Ordinal) ||
            lower.Contains("given what you know about me", StringComparison.Ordinal) ||
            lower.Contains("from what you know about me", StringComparison.Ordinal) ||
            lower.Contains("considering what you know about me", StringComparison.Ordinal);

        if (referencesKnownSelf)
            return true;

        if (!hasLoadedProfileContext)
            return false;

        var hasPersonalDomainCue =
            lower.Contains("diet", StringComparison.Ordinal) ||
            lower.Contains("meal", StringComparison.Ordinal) ||
            lower.Contains("nutrition", StringComparison.Ordinal) ||
            lower.Contains("food", StringComparison.Ordinal) ||
            lower.Contains("eat", StringComparison.Ordinal) ||
            lower.Contains("recipe", StringComparison.Ordinal) ||
            lower.Contains("workout", StringComparison.Ordinal) ||
            lower.Contains("exercise", StringComparison.Ordinal) ||
            lower.Contains("training", StringComparison.Ordinal) ||
            lower.Contains("routine", StringComparison.Ordinal) ||
            lower.Contains("habit", StringComparison.Ordinal) ||
            lower.Contains("sleep", StringComparison.Ordinal) ||
            lower.Contains("focus", StringComparison.Ordinal) ||
            lower.Contains("productivity", StringComparison.Ordinal);

        if (!hasPersonalDomainCue)
            return false;

        return lower.Contains("for me", StringComparison.Ordinal) ||
               lower.Contains(" me ", StringComparison.Ordinal) ||
               lower.StartsWith("me ", StringComparison.Ordinal) ||
               lower.EndsWith(" me", StringComparison.Ordinal) ||
               lower.StartsWith("i ", StringComparison.Ordinal) ||
               lower.Contains(" i ", StringComparison.Ordinal) ||
               lower.StartsWith("i'm ", StringComparison.Ordinal) ||
               lower.StartsWith("i'd ", StringComparison.Ordinal) ||
               lower.StartsWith("my ", StringComparison.Ordinal) ||
               lower.Contains(" my ", StringComparison.Ordinal);
    }

    private async Task<(IReadOnlyList<SelfMemoryFact> Facts, string? Error)> LoadSelfScopedFactsAsync(
        string? activeProfileId,
        IList<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        var argsJson = JsonSerializer.Serialize(new
        {
            skip = 0,
            limit = 50
        });

        var listCall = await CallToolWithAliasAsync(
            MemoryListFactsToolName,
            MemoryListFactsToolNameAlt,
            argsJson,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = listCall.ToolName,
            Arguments = argsJson,
            Result = listCall.Result,
            Success = listCall.Success
        });

        if (!listCall.Success)
            return ([], "memory list facts unavailable");

        try
        {
            using var doc = JsonDocument.Parse(listCall.Result);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errEl) &&
                errEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(errEl.GetString()))
            {
                return ([], errEl.GetString());
            }

            var facts = ExtractSelfScopedFacts(root, activeProfileId)
                .OrderByDescending(f => f.UpdatedAtUtc)
                .Take(12)
                .ToList();
            return (facts, null);
        }
        catch
        {
            return ([], "could not parse memory facts");
        }
    }

    private async Task<(string ToolName, string Result, bool Success)> CallToolWithAliasAsync(
        string primaryToolName,
        string alternateToolName,
        string argsJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mcp.CallToolAsync(primaryToolName, argsJson, cancellationToken);
            return (primaryToolName, result, true);
        }
        catch (Exception primaryError)
        {
            try
            {
                var result = await _mcp.CallToolAsync(alternateToolName, argsJson, cancellationToken);
                return (alternateToolName, result, true);
            }
            catch (Exception alternateError)
            {
                var errorText = $"Error: {primaryError.Message}; fallback failed: {alternateError.Message}";
                return (primaryToolName, errorText, false);
            }
        }
    }

    private sealed record SelfMemoryFact(
        string Subject,
        string Predicate,
        string Object,
        string? ProfileId,
        DateTimeOffset UpdatedAtUtc);

    private static IReadOnlyList<SelfMemoryFact> ExtractSelfScopedFacts(
        JsonElement root,
        string? activeProfileId)
    {
        var output = new List<SelfMemoryFact>();
        if (!root.TryGetProperty("facts", out var factsEl) ||
            factsEl.ValueKind != JsonValueKind.Array)
        {
            return output;
        }

        var hasActiveProfile = !string.IsNullOrWhiteSpace(activeProfileId);
        foreach (var item in factsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var subject = GetString(item, "subject");
            var predicate = GetString(item, "predicate");
            var obj = GetString(item, "object");
            var profileId = GetString(item, "profile_id");

            if (string.IsNullOrWhiteSpace(predicate) || string.IsNullOrWhiteSpace(obj))
                continue;

            var include = hasActiveProfile
                ? string.Equals(profileId, activeProfileId, StringComparison.OrdinalIgnoreCase) ||
                  (string.IsNullOrWhiteSpace(profileId) && IsSelfSubject(subject))
                : IsSelfSubject(subject);

            if (!include)
                continue;

            var updatedAtUtc = DateTimeOffset.MinValue;
            var updatedRaw = GetString(item, "updated_at");
            if (!string.IsNullOrWhiteSpace(updatedRaw) &&
                DateTimeOffset.TryParse(updatedRaw, out var parsedUpdated))
            {
                updatedAtUtc = parsedUpdated;
            }

            output.Add(new SelfMemoryFact(
                subject ?? "user",
                predicate!,
                obj!,
                profileId,
                updatedAtUtc));
        }

        return output;
    }

    private static string BuildSelfMemorySummaryText(IReadOnlyList<SelfMemoryFact> facts)
    {
        if (facts.Count == 0)
        {
            return "I pulled your profile, but I don't yet have many detailed saved facts about you. " +
                   "If you want, tell me your preferences, projects, goals, or routines and I'll keep them handy.";
        }

        var rendered = facts
            .Select(RenderSelfMemoryFact)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (rendered.Count == 0)
            return "I pulled your profile, but there aren't any clear fact entries to summarize yet.";

        var sb = new StringBuilder();
        sb.AppendLine("Here's what I currently know about you:");
        foreach (var line in rendered)
            sb.AppendLine($"- {line}");
        sb.AppendLine();
        sb.Append("If you'd like, I can update or forget any of these.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildSelfMemoryContextBlock(IReadOnlyList<SelfMemoryFact> facts)
    {
        if (facts.Count == 0)
            return "";

        var rendered = facts
            .Select(RenderSelfMemoryFact)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        if (rendered.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("[USER_MEMORY_FACTS]");
        foreach (var line in rendered)
            sb.AppendLine($"- {line}");
        sb.AppendLine("[/USER_MEMORY_FACTS]");
        sb.AppendLine("Use these known user facts when the user asks for personalized advice.");
        return sb.ToString().TrimEnd();
    }

    private static string RenderSelfMemoryFact(SelfMemoryFact fact)
    {
        var subject = (fact.Subject ?? "user").Trim();
        var predicate = (fact.Predicate ?? "").Trim();
        var obj = (fact.Object ?? "").Trim();

        if (string.IsNullOrWhiteSpace(predicate) || string.IsNullOrWhiteSpace(obj))
            return "";

        var pred = predicate.Replace('_', ' ').Trim();
        var predLower = pred.ToLowerInvariant();

        if (IsSelfSubject(subject))
        {
            return predLower switch
            {
                "name" or "name is" => $"Your name is {obj}.",
                "likes" => $"You like {obj}.",
                "prefers" => $"You prefer {obj}.",
                "works on" => $"You work on {obj}.",
                "is" => $"You are {obj}.",
                _ => $"You {predLower} {obj}."
            };
        }

        return $"{subject} {predLower} {obj}.";
    }

    private static bool IsSelfSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return true;

        var token = subject.Trim().ToLowerInvariant();
        return token is "user" or "me" or "i" or "myself";
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node))
            return null;
        if (node.ValueKind == JsonValueKind.Null)
            return null;
        if (node.ValueKind != JsonValueKind.String)
            return null;
        return node.GetString();
    }
}
