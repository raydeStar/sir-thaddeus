using System.Text.Json;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// Entity Resolver — Canonicalizes Named Entities Before Search
//
// When a user asks about "Japanese PM XXXX", we need to figure out:
//   1. Is there a named entity? (yes: "Japanese PM XXXX")
//   2. What's the canonical form? (Fumio Kishida / Shigeru Ishiba / etc.)
//   3. What's the disambiguation? ("Prime Minister of Japan")
//
// Process:
//   1. LLM extraction: cheap constrained call → { name, type, hint }
//   2. If entity found and not cached → web_search for canonicalization
//   3. Parse canonical name from top search result titles
//   4. Cache in session for follow-ups
//
// Cost: 1 tiny LLM call + 1 web_search per new entity.
// Cached entities are free on follow-ups.
// ─────────────────────────────────────────────────────────────────────────

public sealed class EntityResolver
{
    private readonly ILlmClient    _llm;
    private readonly IMcpToolClient _mcp;
    private readonly IAuditLogger  _audit;

    // ── Tool names (try both casing conventions) ─────────────────────
    private const string WebSearchToolName    = "web_search";
    private const string WebSearchToolNameAlt = "WebSearch";

    public EntityResolver(ILlmClient llm, IMcpToolClient mcp, IAuditLogger audit)
    {
        _llm   = llm   ?? throw new ArgumentNullException(nameof(llm));
        _mcp   = mcp   ?? throw new ArgumentNullException(nameof(mcp));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>
    /// Extracted entity from the LLM — raw, before canonicalization.
    /// </summary>
    public sealed record ExtractedEntity
    {
        public required string Name { get; init; }
        public string Type { get; init; } = "unknown";  // Person, Org, Place, Topic
        public string Hint { get; init; } = "";          // e.g., "Prime Minister of Japan"
    }

    /// <summary>
    /// Resolved entity — canonical name confirmed via web search.
    /// </summary>
    public sealed record ResolvedEntity
    {
        public required string CanonicalName    { get; init; }
        public string Type                      { get; init; } = "unknown";
        public string Disambiguation            { get; init; } = "";
    }

    /// <summary>
    /// Attempts to extract and canonicalize a named entity from the
    /// user's message. Returns null if no entity is detected or if
    /// resolution fails.
    /// </summary>
    public async Task<ResolvedEntity?> ResolveAsync(
        string userMessage,
        SearchSession session,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        // ── Check session cache first ────────────────────────────────
        if (!string.IsNullOrWhiteSpace(session.LastEntityCanonical))
        {
            // If the user's message mentions the cached entity (or is
            // a follow-up that doesn't introduce a new one), reuse it.
            var cached = session.LastEntityCanonical;
            var lower  = userMessage.ToLowerInvariant();
            if (lower.Contains(cached.ToLowerInvariant()) ||
                SearchModeRouter.IsFollowUpMessage(lower))
            {
                _audit.Append(new AuditEvent
                {
                    Actor  = "agent",
                    Action = "ENTITY_RESOLVED_FROM_CACHE",
                    Result = cached
                });

                return new ResolvedEntity
                {
                    CanonicalName    = cached,
                    Type             = session.LastEntityType ?? "unknown",
                    Disambiguation   = session.LastEntityDisambiguation ?? ""
                };
            }
        }

        // ── Step 1: LLM extraction ───────────────────────────────────
        var extracted = await ExtractEntityAsync(userMessage, ct);
        if (extracted is null)
            return null;

        _audit.Append(new AuditEvent
        {
            Actor  = "agent",
            Action = "ENTITY_EXTRACTED",
            Result = $"{extracted.Name} ({extracted.Type})",
            Details = new Dictionary<string, object>
            {
                ["name"] = extracted.Name,
                ["type"] = extracted.Type,
                ["hint"] = extracted.Hint
            }
        });

        // ── Step 2: Web search for canonicalization ──────────────────
        var canonical = await CanonicalizeAsync(extracted, toolCallsMade, ct);
        if (canonical is null)
        {
            // Canonicalization failed — use the raw extraction as-is
            canonical = new ResolvedEntity
            {
                CanonicalName  = extracted.Name,
                Type           = extracted.Type,
                Disambiguation = extracted.Hint
            };
        }

        // ── Step 3: Cache in session ─────────────────────────────────
        session.LastEntityCanonical      = canonical.CanonicalName;
        session.LastEntityType           = canonical.Type;
        session.LastEntityDisambiguation = canonical.Disambiguation;

        _audit.Append(new AuditEvent
        {
            Actor  = "agent",
            Action = "ENTITY_RESOLVED",
            Result = canonical.CanonicalName,
            Details = new Dictionary<string, object>
            {
                ["type"]            = canonical.Type,
                ["disambiguation"]  = canonical.Disambiguation
            }
        });

        return canonical;
    }

    // ─────────────────────────────────────────────────────────────────
    // Step 1: LLM Entity Extraction
    // ─────────────────────────────────────────────────────────────────

    private async Task<ExtractedEntity?> ExtractEntityAsync(
        string userMessage, CancellationToken ct)
    {
        var systemPrompt =
            "You are an entity extractor. Given a user message, identify the " +
            "primary named entity (person, organization, place, or specific topic) " +
            "if one exists. Return ONLY a JSON object with these fields:\n" +
            "  { \"name\": \"...\", \"type\": \"Person|Org|Place|Topic\", \"hint\": \"...\" }\n" +
            "The hint is a short disambiguation (e.g., \"Prime Minister of Japan\").\n" +
            "If there is NO named entity, return: { \"name\": \"\", \"type\": \"none\", \"hint\": \"\" }\n" +
            "Return ONLY the JSON. No explanation. No markdown.";

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(userMessage)
        };

        try
        {
            var response = await _llm.ChatAsync(messages, tools: null, maxTokensOverride: 128, ct);
            var content  = (response.Content ?? "").Trim();

            // Strip markdown code fences if present
            content = StripCodeFences(content);

            if (string.IsNullOrWhiteSpace(content))
                return null;

            var parsed = JsonSerializer.Deserialize<JsonElement>(content);

            var name = parsed.TryGetProperty("name", out var n) ? n.GetString() : null;
            var type = parsed.TryGetProperty("type", out var t) ? t.GetString() : "unknown";
            var hint = parsed.TryGetProperty("hint", out var h) ? h.GetString() : "";

            if (string.IsNullOrWhiteSpace(name) || type == "none")
                return null;

            return new ExtractedEntity
            {
                Name = name!.Trim(),
                Type = type ?? "unknown",
                Hint = hint ?? ""
            };
        }
        catch (Exception ex)
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "agent",
                Action = "ENTITY_EXTRACTION_FAILED",
                Result = ex.Message
            });
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Step 2: Web Search Canonicalization
    // ─────────────────────────────────────────────────────────────────

    private async Task<ResolvedEntity?> CanonicalizeAsync(
        ExtractedEntity entity,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var query = !string.IsNullOrWhiteSpace(entity.Hint)
            ? $"\"{entity.Name}\" {entity.Hint}"
            : $"\"{entity.Name}\"";

        var args = JsonSerializer.Serialize(new
        {
            query,
            maxResults = 3,
            recency    = "any"
        });

        string? toolResult = null;
        var toolName = WebSearchToolName;
        var toolOk   = false;

        try
        {
            toolResult = await _mcp.CallToolAsync(toolName, args, ct);
            toolOk = true;
        }
        catch
        {
            try
            {
                toolName   = WebSearchToolNameAlt;
                toolResult = await _mcp.CallToolAsync(toolName, args, ct);
                toolOk = true;
            }
            catch (Exception ex)
            {
                _audit.Append(new AuditEvent
                {
                    Actor  = "agent",
                    Action = "ENTITY_CANONICALIZATION_FAILED",
                    Result = ex.Message
                });
                return null;
            }
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName  = toolName,
            Arguments = args,
            Result    = toolResult?.Length > 200 ? toolResult[..200] + "…" : toolResult ?? "",
            Success   = toolOk
        });

        if (string.IsNullOrWhiteSpace(toolResult))
            return null;

        // Parse canonical name from search results.
        // Look for Wikipedia-style "Name - Description" patterns
        // in the first few result titles.
        var canonical = TryExtractCanonicalFromResults(toolResult, entity);

        return canonical ?? new ResolvedEntity
        {
            CanonicalName  = entity.Name,
            Type           = entity.Type,
            Disambiguation = entity.Hint
        };
    }

    /// <summary>
    /// Attempts to extract a canonical name from search result titles.
    /// Looks for patterns like "Fumio Kishida - Wikipedia" or
    /// "Fumio Kishida | Prime Minister of Japan".
    /// </summary>
    private static ResolvedEntity? TryExtractCanonicalFromResults(
        string toolResult, ExtractedEntity entity)
    {
        // Parse the numbered result entries from the tool output.
        // Format: 1. "Title" — source.com
        var lines = toolResult.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 5) continue;

            // Match numbered entries: "1. "Title" — source"
            if (!char.IsDigit(trimmed[0])) continue;
            var dotIdx = trimmed.IndexOf('.');
            if (dotIdx < 0 || dotIdx > 3) continue;

            var body = trimmed[(dotIdx + 1)..].Trim().Trim('"');

            // Strip " — source.com" suffix
            var dashIdx = body.IndexOf(" — ", StringComparison.Ordinal);
            var title = dashIdx > 0 ? body[..dashIdx].Trim() : body.Trim();

            // Wikipedia pattern: "Name - Wikipedia"
            var wikiIdx = title.IndexOf(" - Wikipedia", StringComparison.OrdinalIgnoreCase);
            if (wikiIdx > 0)
            {
                var canonical = title[..wikiIdx].Trim();
                if (canonical.Length >= 2)
                {
                    return new ResolvedEntity
                    {
                        CanonicalName  = canonical,
                        Type           = entity.Type,
                        Disambiguation = entity.Hint
                    };
                }
            }

            // Pipe pattern: "Name | Description"
            var pipeIdx = title.IndexOf(" | ", StringComparison.Ordinal);
            if (pipeIdx > 0)
            {
                var canonical = title[..pipeIdx].Trim();
                var desc      = title[(pipeIdx + 3)..].Trim();
                if (canonical.Length >= 2)
                {
                    return new ResolvedEntity
                    {
                        CanonicalName  = canonical,
                        Type           = entity.Type,
                        Disambiguation = !string.IsNullOrWhiteSpace(desc) ? desc : entity.Hint
                    };
                }
            }

            // Dash pattern: "Name – Description"
            var enDashIdx = title.IndexOf(" – ", StringComparison.Ordinal);
            if (enDashIdx > 0)
            {
                var canonical = title[..enDashIdx].Trim();
                if (canonical.Length >= 2)
                {
                    return new ResolvedEntity
                    {
                        CanonicalName  = canonical,
                        Type           = entity.Type,
                        Disambiguation = entity.Hint
                    };
                }
            }

            // If the first title matches the entity hint, use the full title
            if (!string.IsNullOrWhiteSpace(entity.Hint) &&
                title.Contains(entity.Hint, StringComparison.OrdinalIgnoreCase))
            {
                // Extract just the name part before any description
                var commaIdx = title.IndexOf(',');
                var colonIdx = title.IndexOf(':');
                var cutAt = (commaIdx > 0, colonIdx > 0) switch
                {
                    (true, true) => Math.Min(commaIdx, colonIdx),
                    (true, _)    => commaIdx,
                    (_, true)    => colonIdx,
                    _            => -1
                };

                var canonical = cutAt > 2 ? title[..cutAt].Trim() : title.Trim();
                if (canonical.Length >= 2 && canonical.Length <= 80)
                {
                    return new ResolvedEntity
                    {
                        CanonicalName  = canonical,
                        Type           = entity.Type,
                        Disambiguation = entity.Hint
                    };
                }
            }
        }

        return null;
    }

    private static string StripCodeFences(string content)
    {
        if (content.StartsWith("```"))
        {
            var endIdx = content.IndexOf('\n');
            if (endIdx > 0) content = content[(endIdx + 1)..];
        }
        if (content.EndsWith("```"))
            content = content[..^3];
        return content.Trim();
    }
}
