using System.Text;
using System.Text.RegularExpressions;
using SirThaddeus.Wiki;

namespace Thaddeus.Runtime.Wiki;

/// <summary>
/// Retrieves sibling wiki pages relevant to a query, scoped to a wiki root/book.
///
/// Silo rule: a wiki root is the boundary. A book can keep Characters, World,
/// Chapters, Plot, and Lore in separate folders while still letting those folders
/// reference each other. Content from a different root never bleeds in.
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
    private const int MaxContextTerms = 32;

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

        var weightedTerms = BuildWeightedTerms(query, currentPage);
        if (weightedTerms.Count == 0) return Array.Empty<RetrievedSiblingPage>();

        var tree = await _wiki.GetTreeAsync(currentPage.Page.RootId, cancellationToken).ConfigureAwait(false);
        if (tree is null) return Array.Empty<RetrievedSiblingPage>();

        var candidates = SelectBookCandidates(tree, currentPage.Page);
        if (candidates.Count == 0) return Array.Empty<RetrievedSiblingPage>();

        var scored = new List<ScoredCandidate>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await _wiki.GetPageAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
            if (document is null) continue;
            var score = Score(document, weightedTerms);
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
            var snippet = BuildSnippet(item.Document.Markdown, weightedTerms.Keys, Math.Min(SnippetWindowChars, remaining));
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

    private static IReadOnlyList<WikiPage> SelectBookCandidates(WikiTree tree, WikiPage currentPage)
        => tree.Pages
            .Where(page => page.Id != currentPage.Id)
            .ToArray();

    private static Dictionary<string, double> BuildWeightedTerms(string query, WikiPageDocument currentPage)
    {
        var terms = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in ExtractQueryTerms(query))
        {
            terms[term] = 1.0;
        }

        foreach (var term in ExtractCurrentPageContextTerms(currentPage).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxContextTerms))
        {
            if (!terms.ContainsKey(term))
                terms[term] = 0.35;
        }

        return terms;
    }

    private static IEnumerable<string> ExtractCurrentPageContextTerms(WikiPageDocument currentPage)
    {
        foreach (var term in ExtractQueryTerms(currentPage.Page.Title))
            yield return term;

        foreach (Match match in HeadingLine.Matches(currentPage.Markdown))
        {
            foreach (var term in ExtractQueryTerms(match.Groups["text"].Value))
                yield return term;
        }

        foreach (var term in ExtractCapitalizedTerms(currentPage.Markdown))
            yield return term;
    }

    private static IEnumerable<string> ExtractCapitalizedTerms(string markdown)
    {
        foreach (var raw in TokenSplit.Split(markdown))
        {
            if (raw.Length < 3 || Stopwords.Contains(raw)) continue;
            if (char.IsUpper(raw[0])) yield return raw.ToLowerInvariant();
        }
    }

    private static PageScore Score(WikiPageDocument document, IReadOnlyDictionary<string, double> weightedTerms)
    {
        var titleHits = CountDistinctTermHits(document.Page.Title, weightedTerms);
        var headingHits = 0d;
        foreach (Match match in HeadingLine.Matches(document.Markdown))
        {
            headingHits += CountDistinctTermHits(match.Groups["text"].Value, weightedTerms);
        }

        var bodyHits = 0d;
        foreach (var (term, weight) in weightedTerms)
        {
            // Cap per-term contributions so a giant page that mentions one term 200 times
            // doesn't drown out a focused page that hits three terms cleanly.
            bodyHits += Math.Min(8, CountTermOccurrences(document.Markdown, term)) * weight;
        }

        // Title is the strongest signal for retrieval — a "Characters" page should win for
        // a question about a character even if its body is shorter than a chapter.
        var total = titleHits * 4 + headingHits * 2 + bodyHits;
        return new PageScore(titleHits, headingHits, bodyHits, total);
    }

    private static double CountDistinctTermHits(string text, IReadOnlyDictionary<string, double> terms)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var hits = 0d;
        foreach (var (term, weight) in terms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase)) hits += weight;
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

    private readonly record struct PageScore(double TitleHits, double HeadingHits, double BodyHits, double Total);

    private readonly record struct ScoredCandidate(WikiPageDocument Document, PageScore Score);
}

public sealed record RetrievedSiblingPage(WikiPage Page, string Snippet, double Score);
