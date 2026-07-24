using System.Text.Json;
using SirThaddeus.Agent;

namespace SirThaddeus.DirectEval;

internal sealed class DirectEvalState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IMcpToolClient _mcp;
    private readonly string _filesRoot;

    public DirectEvalState(IMcpToolClient mcp, string filesRoot)
    {
        _mcp = mcp;
        _filesRoot = Path.GetFullPath(filesRoot);
    }

    public async Task ApplyAsync(DirectStateSetup setup, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_filesRoot);
        foreach (var file in setup.Files)
        {
            var path = ResolveFile(file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = file.ContentBase64 is null
                ? System.Text.Encoding.UTF8.GetBytes(file.Content)
                : Convert.FromBase64String(file.ContentBase64);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        }

        foreach (var root in setup.WikiRoots)
        {
            using var created = await CallJsonAsync(
                "wiki_root_create", new { name = root.Name }, cancellationToken).ConfigureAwait(false);
            var rootId = created.RootElement.GetProperty("root").GetProperty("id").GetString()
                ?? throw new InvalidDataException("wiki_root_create omitted root.id");
            foreach (var page in root.Pages)
            {
                using var _ = await CallJsonAsync(
                    "wiki_page_create",
                    new { rootId, title = page.Title, markdown = page.Markdown },
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<Dictionary<string, object>?> ObserveAsync(
        IReadOnlyList<DirectObservation> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
            return null;

        var state = new Dictionary<string, object>(StringComparer.Ordinal);
        var wikiRequests = requests.Where(request =>
            string.Equals(request.Type, "wiki", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (wikiRequests.Length > 0)
        {
            var selectedNames = wikiRequests.SelectMany(request => request.RootNames)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal);
            using var rootsDocument = await CallJsonAsync(
                "wiki_roots_list", new { }, cancellationToken).ConfigureAwait(false);
            var roots = new List<object>();
            foreach (var root in rootsDocument.RootElement.GetProperty("roots").EnumerateArray())
            {
                var name = root.GetProperty("name").GetString() ?? string.Empty;
                if (selectedNames.Count > 0 && !selectedNames.Contains(name))
                    continue;
                var rootId = root.GetProperty("id").GetString()
                    ?? throw new InvalidDataException("wiki_roots_list omitted root.id");
                using var tree = await CallJsonAsync(
                    "wiki_tree_get", new { rootId, maxItems = 500 }, cancellationToken)
                    .ConfigureAwait(false);
                var pages = new List<object>();
                foreach (var page in tree.RootElement.GetProperty("tree").GetProperty("pages").EnumerateArray())
                {
                    var pageId = page.GetProperty("id").GetString()
                        ?? throw new InvalidDataException("wiki_tree_get omitted page.id");
                    using var body = await CallJsonAsync(
                        "wiki_page_read", new { pageId, maxChars = 60_000 }, cancellationToken)
                        .ConfigureAwait(false);
                    var document = body.RootElement.GetProperty("document");
                    pages.Add(new
                    {
                        title = document.GetProperty("page").GetProperty("title").GetString() ?? string.Empty,
                        markdown = document.GetProperty("markdown").GetString() ?? string.Empty
                    });
                }
                roots.Add(new { name, pages = pages.OrderBy(PageTitle, StringComparer.Ordinal).ToArray() });
            }
            state["wiki"] = new { roots = roots.OrderBy(RootName, StringComparer.Ordinal).ToArray() };
        }

        var filePaths = requests.Where(request =>
                string.Equals(request.Type, "files", StringComparison.OrdinalIgnoreCase))
            .SelectMany(request => request.Paths)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (filePaths.Length > 0)
        {
            state["files"] = new
            {
                entries = filePaths.Select(relativePath =>
                {
                    var fullPath = ResolveFile(relativePath);
                    var exists = File.Exists(fullPath);
                    return new
                    {
                        path = relativePath.Replace('\\', '/'),
                        exists,
                        content = exists ? File.ReadAllText(fullPath) : null
                    };
                }).ToArray()
            };
        }
        return state;
    }

    private async Task<JsonDocument> CallJsonAsync(
        string tool,
        object arguments,
        CancellationToken cancellationToken)
    {
        var raw = await _mcp.CallToolAsync(
            tool, JsonSerializer.Serialize(arguments, JsonOptions), cancellationToken).ConfigureAwait(false);
        var document = JsonDocument.Parse(raw);
        if (document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var error = document.RootElement.TryGetProperty("error", out var errorValue)
                ? errorValue.GetString()
                : raw;
            document.Dispose();
            throw new InvalidOperationException($"{tool} setup/observation failed: {error}");
        }
        return document;
    }

    private string ResolveFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Direct-eval fixture paths must be non-empty and relative.");
        var root = _filesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(relativePath, root);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Direct-eval fixture path escapes the isolated files root.");
        return fullPath;
    }

    private static string RootName(object value) =>
        (string)(value.GetType().GetProperty("name")?.GetValue(value) ?? string.Empty);

    private static string PageTitle(object value) =>
        (string)(value.GetType().GetProperty("title")?.GetValue(value) ?? string.Empty);
}

internal sealed record DirectStateSetup
{
    public List<DirectWikiRootSetup> WikiRoots { get; init; } = [];
    public List<DirectFileSetup> Files { get; init; } = [];
}

internal sealed record DirectWikiRootSetup
{
    public required string Name { get; init; }
    public List<DirectWikiPageSetup> Pages { get; init; } = [];
}

internal sealed record DirectWikiPageSetup
{
    public required string Title { get; init; }
    public string Markdown { get; init; } = string.Empty;
}

internal sealed record DirectFileSetup
{
    public required string Path { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? ContentBase64 { get; init; }
}

internal sealed record DirectObservation
{
    public required string Type { get; init; }
    public List<string> RootNames { get; init; } = [];
    public List<string> Paths { get; init; } = [];
}
