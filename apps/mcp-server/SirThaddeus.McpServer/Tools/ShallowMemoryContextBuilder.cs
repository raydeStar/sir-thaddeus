using System.Text;
using System.Text.Json;
using SirThaddeus.Memory;

namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// Builds the "shallow memory" context block injected before the deep
/// memory pack. Output is a compact, clearly-labeled text block:
///
///   [PROFILE]
///   Name: Sample User | Pronouns: he/him | TZ: US-Central
///   [/PROFILE]
///   [NUGGETS]
///   • User often asks for help with math homework.
///   • User prefers blunt, practical feedback.
///   [/NUGGETS]
///
/// Hard-capped at ~250 chars for the profile block and 2–5 nuggets
/// depending on mode. Deterministic — no embeddings, no LLM calls.
/// </summary>
internal static class ShallowMemoryContextBuilder
{
    /// <summary>Max character length for the rendered PROFILE section.</summary>
    private const int ProfileMaxChars = 300;

    /// <summary>
    /// Builds the combined shallow-memory text block from a user profile,
    /// an optional other-person profile, and scored nuggets.
    /// Returns empty string when no profile and no nuggets exist.
    /// </summary>
    public static string Build(
        ProfileCard?              userProfile,
        ProfileCard?              otherProfile,
        IReadOnlyList<MemoryNugget> nuggets)
    {
        if (userProfile is null && nuggets.Count == 0 && otherProfile is null)
            return "";

        var sb = new StringBuilder();

        // ── Profile block ────────────────────────────────────────────
        if (userProfile is not null)
        {
            sb.AppendLine("[PROFILE]");
            sb.AppendLine(RenderProfileCard(userProfile));

            if (otherProfile is not null)
            {
                sb.AppendLine();
                sb.AppendLine(RenderPersonCard(otherProfile));
            }

            sb.AppendLine("[/PROFILE]");

            // Tell the LLM to actually use the name — small models
            // often ignore context unless explicitly told what to do.
            var firstName = userProfile.DisplayName.Split(' ', 2)[0];
            sb.AppendLine(
                $"You know this user as \"{firstName}\" — " +
                "address them by name naturally.");
        }

        // ── Nuggets block ────────────────────────────────────────────
        if (nuggets.Count > 0)
        {
            sb.AppendLine("[NUGGETS]");
            foreach (var n in nuggets)
                sb.AppendLine($"  \u2022 {n.Text}");
            sb.AppendLine("[/NUGGETS]");
        }

        return sb.ToString().TrimEnd();
    }

    // ─────────────────────────────────────────────────────────────────
    // Card Renderers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the user's profile card into a compact single/multi-line
    /// summary pulled from profile_json. Falls back to display_name only
    /// if the JSON is empty or malformed.
    /// </summary>
    internal static string RenderProfileCard(ProfileCard card)
    {
        var parts = new List<string> { $"Name: {card.DisplayName}" };

        try
        {
            using var doc = JsonDocument.Parse(card.ProfileJson);
            var root = doc.RootElement;

            TryAdd(root, "preferred_name", v => parts.Add($"Call me: {v}"));
            TryAdd(root, "pronouns",       v => parts.Add($"Pronouns: {v}"));
            TryAdd(root, "timezone",       v => parts.Add($"TZ: {v}"));
            TryAdd(root, "style",          v => parts.Add($"Style: {v}"));

            // Privacy: "never_mention" is enforced at retrieval time,
            // but also useful as a reminder in the injected block.
            TryAdd(root, "never_mention",  v => parts.Add($"[NEVER mention: {v}]"));
        }
        catch
        {
            // Malformed JSON — stick with display name only
        }

        var rendered = string.Join(" | ", parts);

        // Hard-cap to avoid ballooning the context window
        return rendered.Length > ProfileMaxChars
            ? rendered[..ProfileMaxChars] + "\u2026"
            : rendered;
    }

    /// <summary>
    /// Renders a non-user person card into a compact one-liner:
    /// "Dante (son): age 5, loves dinosaurs"
    /// Only includes a few highlights to stay well under budget.
    /// </summary>
    internal static string RenderPersonCard(ProfileCard card)
    {
        var label = !string.IsNullOrWhiteSpace(card.Relationship)
            ? $"{card.DisplayName} ({card.Relationship})"
            : card.DisplayName;

        var highlights = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(card.ProfileJson);
            var root = doc.RootElement;

            // Pull out at most 2 quick highlights to keep it tight
            TryAdd(root, "age",       v => highlights.Add($"age {v}"));
            TryAdd(root, "highlight", v => highlights.Add(v));
            TryAdd(root, "notes",     v => highlights.Add(v));
        }
        catch
        {
            // Malformed JSON — just the label
        }

        var suffix = highlights.Count > 0
            ? ": " + string.Join(", ", highlights.Take(2))
            : "";

        return label + suffix;
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static void TryAdd(
        JsonElement root, string property, Action<string> addAction)
    {
        if (root.TryGetProperty(property, out var el))
        {
            var val = el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : el.GetRawText();

            if (!string.IsNullOrWhiteSpace(val))
                addAction(val);
        }
    }
}
