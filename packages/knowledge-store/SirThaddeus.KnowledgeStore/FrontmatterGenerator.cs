using SirThaddeus.LlmClient;

namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Generates YAML frontmatter by asking the LLM to extract
/// metadata from file content. Runs async after file writes.
/// </summary>
public sealed class FrontmatterGenerator
{
    private readonly ILlmClient _llm;
    private readonly FrontmatterParser _parser;

    public FrontmatterGenerator(ILlmClient llm, FrontmatterParser parser)
    {
        _llm = llm;
        _parser = parser;
    }

    /// <summary>
    /// Generate frontmatter metadata for a file's content.
    /// Uses a focused LLM call with small input and structured output.
    /// </summary>
    public async Task<Frontmatter> GenerateAsync(
        string content,
        string domain,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        // Truncate content to keep the LLM call cheap
        var truncated = content.Length > 2000
            ? content[..2000]
            : content;

        var prompt = $"""
            Extract metadata from this content.
            Respond with ONLY valid YAML, no other text.

            Rules:
            - tags: 3-7 lowercase kebab-case topic tags
            - mentions: named entities (people, places, items, concepts)
              as lowercase kebab-case. Omit if none.
            - summary: 1-2 sentences, max 50 words. Be specific:
              name names, state what happened, include key facts.
              BAD: "Character does stuff in a place."
              GOOD: "Ennix leads the assault on the western gate.
              First use of shadow ability. Lyra injured."
            - type: one of [scene, character, note, log, entry,
              reference, rule, plan, record]

            Domain: {domain}
            Filename: {fileName}

            Content:
            {truncated}

            Respond:
            tags: [tag1, tag2, tag3]
            mentions: [entity1, entity2]
            summary: "your summary here"
            type: file_type
            """;

        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You extract metadata from text. Respond with ONLY valid YAML."),
            ChatMessage.User(prompt)
        };

        var response = await _llm.ChatAsync(messages, tools: null, maxTokensOverride: 256, cancellationToken);

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            return new Frontmatter
            {
                Created = DateTime.UtcNow,
                Updated = DateTime.UtcNow,
                Type = "note"
            };
        }

        return ParseLlmResponse(response.Content);
    }

    /// <summary>
    /// Process the next file in the tagging queue.
    /// Reads the file, generates frontmatter, and writes it back.
    /// </summary>
    public async Task ProcessQueueItemAsync(
        string rootPath,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        if (!File.Exists(fullPath))
            return;

        // Check if file already has frontmatter
        var existing = await _parser.ReadFrontmatterOnlyAsync(fullPath);
        if (existing is not null)
            return; // Already tagged

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
            return;

        var domain = GetDomainFromPath(relativePath);
        var fileName = Path.GetFileName(relativePath);

        var frontmatter = await GenerateAsync(content, domain, fileName, cancellationToken);
        await _parser.WriteFrontmatterAsync(fullPath, frontmatter);
    }

    private static string GetDomainFromPath(string relativePath)
    {
        var parts = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[0] : "general";
    }

    private static Frontmatter ParseLlmResponse(string yamlText)
    {
        // Strip any markdown code fences the LLM might have added
        var cleaned = yamlText.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = cleaned.IndexOf('\n');
            if (firstNewline >= 0)
                cleaned = cleaned[(firstNewline + 1)..];
        }
        if (cleaned.EndsWith("```", StringComparison.Ordinal))
            cleaned = cleaned[..^3];

        cleaned = cleaned.Trim();

        var lines = cleaned.Split('\n').ToList();
        var parsed = FrontmatterParser.ParseYaml(lines);

        return parsed ?? new Frontmatter
        {
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow,
            Type = "note"
        };
    }
}
