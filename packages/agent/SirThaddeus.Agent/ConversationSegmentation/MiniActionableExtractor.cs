using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.ConversationSegmentation;

/// <summary>
/// Low-confidence fallback that extracts actionable spans by offset.
/// Returns offsets into the original message (never paraphrases).
/// </summary>
public sealed class MiniActionableExtractor
{
    private readonly ILlmClient _llm;

    public MiniActionableExtractor(ILlmClient llm)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    public async Task<IReadOnlyList<ConversationSegment>> TryExtractAsync(
        string originalMessage,
        int maxActionables,
        CancellationToken cancellationToken = default)
    {
        var message = originalMessage ?? "";
        if (string.IsNullOrWhiteSpace(message) || maxActionables <= 0)
            return [];

        var system = """
                     Extract up to N actionable request spans from the user message.
                     Return ONLY JSON with this exact schema:
                     {
                       "actionables": [
                         {"startIndex": 0, "endIndex": 12}
                       ]
                     }

                     Rules:
                     - startIndex and endIndex must be offsets into the ORIGINAL message.
                     - endIndex is exclusive.
                     - Do NOT paraphrase.
                     - Do NOT include social-only text unless it contains an actionable request.
                     - Keep original order.
                     - Return at most N items.
                     """;

        var user = $"N={maxActionables}\nMESSAGE:\n{message}";
        var response = await _llm.ChatAsync(
            [ChatMessage.System(system), ChatMessage.User(user)],
            tools: null,
            maxTokensOverride: 180,
            cancellationToken);

        var raw = StripCodeFences((response.Content ?? "").Trim());
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("actionables", out var actionables) ||
                actionables.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<ConversationSegment>();
            var order = 0;
            foreach (var item in actionables.EnumerateArray())
            {
                if (!item.TryGetProperty("startIndex", out var startElement) ||
                    !item.TryGetProperty("endIndex", out var endElement) ||
                    startElement.ValueKind != JsonValueKind.Number ||
                    endElement.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var start = startElement.GetInt32();
                var end = endElement.GetInt32();
                if (!IsValidRange(start, end, message.Length))
                    continue;

                var text = message[start..end].Trim();
                if (text.Length < 2)
                    continue;

                result.Add(new ConversationSegment
                {
                    SegmentId = $"seg-fallback-{order + 1:0000}",
                    Text = text,
                    Order = order,
                    StartIndex = start,
                    EndIndex = end,
                    IsActionable = true,
                    Confidence = 0.60
                });
                order++;

                if (result.Count >= maxActionables)
                    break;
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private static bool IsValidRange(int start, int end, int length)
        => start >= 0 && end > start && end <= length;

    private static string StripCodeFences(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var lines = text.Split('\n');
        if (lines.Length < 2)
            return text.Trim('`', '\n', '\r');

        var first = 1;
        var last = lines.Length - 1;
        if (!lines[last].TrimStart().StartsWith("```", StringComparison.Ordinal))
            last = lines.Length;

        return string.Join('\n', lines[first..last]).Trim();
    }
}

