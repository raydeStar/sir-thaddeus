using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Parses and serializes YAML frontmatter from markdown files.
/// Reads only the frontmatter block (between --- delimiters),
/// never loads the file body.
/// </summary>
public sealed class FrontmatterParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    /// Read ONLY the YAML frontmatter from a file.
    /// Stops at the closing '---'. Never loads the body.
    /// </summary>
    public async Task<Frontmatter?> ReadFrontmatterOnlyAsync(string filePath)
    {
        var yamlLines = new List<string>();
        var inFrontmatter = false;

        using var reader = new StreamReader(filePath);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!inFrontmatter)
            {
                if (line.TrimEnd() == "---")
                {
                    inFrontmatter = true;
                    continue;
                }
                else
                {
                    return null; // No frontmatter
                }
            }

            if (line.TrimEnd() == "---")
                break; // End of frontmatter — stop reading

            yamlLines.Add(line);
        }

        return yamlLines.Count > 0 ? ParseYaml(yamlLines) : null;
    }

    /// <summary>
    /// Read the body of a file (everything after the frontmatter block).
    /// </summary>
    public async Task<string> ReadBodyAsync(string filePath)
    {
        var pastFrontmatter = false;
        var inFrontmatter = false;
        var lines = new List<string>();

        using var reader = new StreamReader(filePath);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (pastFrontmatter)
            {
                lines.Add(line);
                continue;
            }

            if (!inFrontmatter)
            {
                if (line.TrimEnd() == "---")
                {
                    inFrontmatter = true;
                    continue;
                }
                else
                {
                    // No frontmatter; everything is body
                    lines.Add(line);
                    pastFrontmatter = true;
                    continue;
                }
            }

            if (line.TrimEnd() == "---")
            {
                pastFrontmatter = true;
                continue;
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Prepend or replace the frontmatter block at the top of a file.
    /// Preserves the file body.
    /// </summary>
    public async Task WriteFrontmatterAsync(string filePath, Frontmatter frontmatter)
    {
        var body = await ReadBodyAsync(filePath);
        var yaml = SerializeToYaml(frontmatter);
        var content = $"---\n{yaml}---\n{body}";
        await File.WriteAllTextAsync(filePath, content);
    }

    /// <summary>
    /// Serialize a Frontmatter object to YAML text.
    /// </summary>
    public string SerializeToYaml(Frontmatter frontmatter)
    {
        // Build a dictionary for controlled output ordering
        var dict = new Dictionary<string, object>();

        if (frontmatter.Tags.Count > 0)
            dict["tags"] = frontmatter.Tags;

        if (frontmatter.Mentions.Count > 0)
            dict["mentions"] = frontmatter.Mentions;

        if (!string.IsNullOrEmpty(frontmatter.Summary))
            dict["summary"] = frontmatter.Summary;

        dict["created"] = frontmatter.Created.ToString("yyyy-MM-dd");
        dict["updated"] = frontmatter.Updated.ToString("yyyy-MM-dd");

        if (!string.IsNullOrEmpty(frontmatter.Type))
            dict["type"] = frontmatter.Type;

        return Serializer.Serialize(dict);
    }

    /// <summary>
    /// Parse YAML lines into a Frontmatter object.
    /// </summary>
    public static Frontmatter? ParseYaml(List<string> yamlLines)
    {
        try
        {
            var yaml = string.Join('\n', yamlLines);
            var raw = Deserializer.Deserialize<Dictionary<string, object>>(yaml);
            if (raw is null) return null;

            return new Frontmatter
            {
                Tags = ExtractStringList(raw, "tags"),
                Mentions = ExtractStringList(raw, "mentions"),
                Summary = ExtractString(raw, "summary"),
                Created = ExtractDate(raw, "created"),
                Updated = ExtractDate(raw, "updated"),
                Type = ExtractString(raw, "type") ?? "note"
            };
        }
        catch (Exception ex)
        {
            // A non-YAML file (plain markdown, mis-quoted YAML, etc.) legitimately
            // has no frontmatter — this is expected. Debug-level log keeps the
            // failure traceable without spamming.
            Log.ForContext(typeof(FrontmatterParser))
                .Debug(ex, "Frontmatter YAML failed to parse; treating as no-frontmatter");
            return null;
        }
    }

    private static List<string> ExtractStringList(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value))
            return [];

        if (value is List<object> list)
            return list.Select(o => o?.ToString() ?? "").Where(s => s.Length > 0).ToList();

        if (value is string s)
            return [s];

        return [];
    }

    private static string ExtractString(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var value) && value is string s)
            return s;
        return string.Empty;
    }

    private static DateTime ExtractDate(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var value))
        {
            if (value is DateTime dt) return dt;
            if (value is string s && DateTime.TryParse(s, out var parsed))
                return parsed;
        }
        return DateTime.UtcNow;
    }
}
