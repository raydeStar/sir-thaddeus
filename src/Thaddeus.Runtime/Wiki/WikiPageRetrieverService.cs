using System.Text;
using System.Text.RegularExpressions;
using SirThaddeus.Wiki;

namespace Thaddeus.Runtime.Wiki;

/// <summary>
/// Retrieves sibling wiki pages relevant to a query, scoped to a folder silo.
///
/// Silo rule: a page that belongs to a folder only "sees" pages in that folder's
/// subtree. A page at the root level only "sees" other root-level pages. Cross-folder
/// content never bleeds in. This matches the user's mental model where folders like
/// BOOK/Characters and BOOK/World are independent contexts that can each get bigger
/// than the prompt budget without polluting each other.
///
/// Scoring is intentionally simple (token-overlap with title/heading boost) so the
/// implementation has zero new persisted state and zero schema migration risk.
/// The contract is built so it can be swapped for SQLite FTS5/BM25 or vector
/// similarity later without changing callers.
/// </summary>
public sealed class WikiPageRetrieverService
{
    private const int SnippetWindowChars = 600;
    private const int MinSiblingSnippetChars = 200;

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "this", "that", "what", "when", "where",
        "which", "while", "into", "have", "has", "had", "are", "was", "were", "but",
        "not", "you", "your", "they", "their", "them", "his", "her", "she", "him",
        "any", "all", "can", "could", "should", "would", "about", "page", "wiki",
        "say", "says", "tell", "give", "make", "made", "use", "uses", "used",
    };

    private static readonly Regex TokenSplit = new(@"[^\p{L}\p{Nd}']+", RegexOptions.Compiled);
    private static readonly Regex HeadingLine = new(@"^\s{0,3}#{1,6}\s+(?<text>.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly IWikiStore _wiki;

    public WikiPageRetrieverService(IWikiStore wiki)
    {
        _wiki = wiki ?? throw new ArgumentNullException(nameof(wiki));
    }

    public async Task<IReadOnlyList<RetrievedSiblingPage>> RetrieveSiblingsAsync(
        WikiPageDocument currentPage,
        string query,
        int charBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentPage);
        if (charBudget <= 0) return Array.Empty<RetrievedSiblingPage>();

        var queryTerms = ExtractQueryTerms(query);
        if (queryTerms.Count == 0) return Array.Empty<RetrievedSiblingPage>();

        var tree = await _wiki.GetTreeAsync(currentPage.Page.RootId, cancellationToken).ConfigureAwait(false);
        if (tree is null) return Array.Empty<RetrievedSiblingPage>();

        var candidates = SelectSiloCandidates(tree, currentPage.Page);
        if (candidates.Count == 0) return Array.Empty<RetrievedSiblingPage>();

        var scored = new List<ScoredCandidate>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await _wiki.GetPageAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
            if (document is null) continue;
            var score = Score(document, queryTerms);
            if (score.Total <= 0) continue;
            scored.Add(new ScoredCandidate(document, score));
        }

        if (scored.Count == 0) return Array.Empty<RetrievedSiblingPage>();
        scored.Sort(static (a, b) => b.Score.Total.CompareTo(a.Score.Total));

        var results = new List<RetrievedSiblingPage>();
        var remaining = charBudget;
        foreach (var item in scored)
        {
            if (remaining < MinSiblingSnippetChars) break;
            var snippet = BuildSnippet(item.Document.Markdown, queryTerms, Math.Min(SnippetWindowChars, remaining));
            if (snippet.Length == 0) continue;
            results.Add(new RetrievedSiblingPage(item.Document.Page, snippet, item.Score.Total));
            remaining -= snippet.Length;
        }
        return results;
    }

    /// <summary>Token set extracted from a free-form query, lowercased and stop-word filtered.</summary>
    internal static IReadOnlyCollection<string> ExtractQueryTerms(string? query)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0) return Array.Empty<string>();

        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in TokenSplit.Split(trimmed))
        {
            if (raw.Length < 3) continue;
            if (Stopwords.Contains(raw)) continue;
            terms.Add(raw.ToLowerInvariant());
        }
        return terms;
    }

    private static IReadOnlyList<WikiPage> SelectSiloCandidates(WikiTree tree, WikiPage currentPage)
    {
        // Folder-scoped page: include every other page whose folder is a descendant of the current folder.
        if (!string.IsNullOrEmpty(currentPage.FolderId))
        {
            var folderIds = DescendantFolderIds(tree, currentPage.FolderId);
            return tree.Pages
                .Where(page => page.Id != currentPage.Id
                    && page.FolderId is not null
                    && folderIds.Contains(page.FolderId))
                .ToArray();
        }

        // Root-level page: only see other root-level pages.
        return tree.Pages
            .Where(page => page.Id != currentPage.Id && string.IsNullOrEmpty(page.FolderId))
            .ToArray();
    }

    private static HashSet<string> DescendantFolderIds(WikiTree tree, string folderId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { folderId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var folder in tree.Folders)
            {
                if (folder.ParentFolderId is not null && ids.Contains(folder.ParentFolderId) && ids.Add(folder.Id))
                    changed = true;
            }
        }
        return ids;
    }

    private static PageScore Score(WikiPageDocument document, IReadOnlyCollection<string> queryTerms)
    {
        var titleHits = CountDistinctTermHits(document.Page.Title, queryTerms);
        var headingHits = 0;
        foreach (Match match in HeadingLine.Matches(document.Markdown))
        {
            headingHits += CountDistinctTermHits(match.Groups["text"].Value, queryTerms);
        }

        var bodyHits = 0;
        foreach (var term in queryTerms)
        {
            // Cap per-term contributions so a giant page that mentions one term 200 times
            // doesn't drown out a focused page that hits three terms cleanly.
            bodyHits += Math.Min(8, CountTermOccurrences(document.Markdown, term));
        }

        // Title is the strongest signal for retrieval — a "Characters" page should win for
        // a question about a character even if its body is shorter than a chapter.
        var total = titleHits * 4 + headingHits * 2 + bodyHits;
        return new PageScore(titleHits, headingHits, bodyHits, total);
    }

    private static int CountDistinctTermHits(string text, IReadOnlyCollection<string> terms)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var hits = 0;
        foreach (var term in terms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase)) hits++;
        }
        return hits;
    }

    private static int CountTermOccurrences(string text, string term)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return 0;
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += term.Length;
        }
        return count;
    }

    /// <summary>
    /// Picks a body window centered on the densest cluster of query-term hits, keeping the
    /// snippet under <paramref name="maxLength"/> characters. Ellipses mark trimmed ends.
    /// </summary>
    internal static string BuildSnippet(string markdown, IReadOnlyCollection<string> queryTerms, int maxLength)
    {
        if (string.IsNullOrEmpty(markdown) || maxLength <= 0) return string.Empty;
        if (markdown.Length <= maxLength) return markdown.Trim();

        var bestStart = 0;
        var bestHits = -1;
        var stride = Math.Max(50, maxLength / 4);
        for (var start = 0; start < markdown.Length; start += stride)
        {
            var window = markdown.AsSpan(start, Math.Min(maxLength, markdown.Length - start));
            var hits = 0;
            foreach (var term in queryTerms)
            {
                if (window.Contains(term, StringComparison.OrdinalIgnoreCase)) hits++;
            }
            if (hits > bestHits)
            {
                bestHits = hits;
                bestStart = start;
            }
        }

        // Snap the window to the nearest paragraph or sentence boundary so the snippet
        // doesn't slice mid-word; cosmetic but materially improves model comprehension.
        bestStart = SnapBackToBoundary(markdown, bestStart);
        var snippetEnd = Math.Min(markdown.Length, bestStart + maxLength);
        snippetEnd = SnapForwardToBoundary(markdown, snippetEnd);

        var builder = new StringBuilder();
        if (bestStart > 0) builder.Append("… ");
        builder.Append(markdown.AsSpan(bestStart, snippetEnd - bestStart).Trim());
        if (snippetEnd < markdown.Length) builder.Append(" …");
        return builder.ToString();
    }

    private static int SnapBackToBoundary(string text, int index)
    {
        if (index <= 0) return 0;
        for (var i = index; i > Math.Max(0, index - 120); i--)
        {
            if (i < text.Length && (text[i] == '\n' || text[i] == '.' || text[i] == ' '))
                return i + 1;
        }
        return index;
    }

    private static int SnapForwardToBoundary(string text, int index)
    {
        if (index >= text.Length) return text.Length;
        for (var i = index; i < Math.Min(text.Length, index + 120); i++)
        {
            if (text[i] == '\n' || text[i] == '.') return i + 1;
        }
        return index;
    }

    private readonly record struct PageScore(int TitleHits, int HeadingHits, int BodyHits, int Total);

    private readonly record struct ScoredCandidate(WikiPageDocument Document, PageScore Score);
}

public sealed record RetrievedSiblingPage(WikiPage Page, string Snippet, double Score);
