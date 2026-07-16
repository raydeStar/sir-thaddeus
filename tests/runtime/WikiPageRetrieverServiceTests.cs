using Microsoft.Extensions.Logging.Abstractions;
using SirThaddeus.Wiki.Storage;
using Thaddeus.Runtime.Wiki;

namespace Thaddeus.Runtime.Tests;

public sealed class WikiPageRetrieverServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalWikiStore _wiki;
    private readonly WikiPageRetrieverService _retriever;

    public WikiPageRetrieverServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-retriever-" + Guid.NewGuid().ToString("N")[..8]);
        _wiki = new LocalWikiStore(_tempDir, NullLogger<LocalWikiStore>.Instance);
        _retriever = new WikiPageRetrieverService(_wiki);
    }

    public void Dispose()
    {
        _wiki.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task Book_root_allows_cross_folder_reference_without_crossing_roots()
    {
        var root = await _wiki.CreateRootAsync("Book", null, CancellationToken.None);
        var characters = await _wiki.CreateFolderAsync(root.Id, "Characters", null, CancellationToken.None);
        var world = await _wiki.CreateFolderAsync(root.Id, "World", null, CancellationToken.None);
        var chapters = await _wiki.CreateFolderAsync(root.Id, "Chapters", null, CancellationToken.None);

        var current = await _wiki.CreatePageAsync(
            root.Id, chapters.Id,
            "Chapter 4",
            "# Chapter 4\n\nKazalt reaches Leviathan Bay while King Edrin waits for news.",
            CancellationToken.None);
        await _wiki.CreatePageAsync(
            root.Id, characters.Id,
            "Kazalt the Knight",
            "# Kazalt\n\nA loyal knight sworn to King Edrin.",
            CancellationToken.None);
        await _wiki.CreatePageAsync(
            root.Id, world.Id,
            "Leviathan Bay",
            "# Leviathan Bay\n\nA storm harbor where the tide exposes ancient stone roads.",
            CancellationToken.None);
        var otherRoot = await _wiki.CreateRootAsync("Other Book", null, CancellationToken.None);
        await _wiki.CreatePageAsync(
            otherRoot.Id, null,
            "Kazalt Mirror",
            "# Kazalt Mirror\n\nThis matching page belongs to a different book and must not appear.",
            CancellationToken.None);

        var siblings = await _retriever.RetrieveSiblingsAsync(
            current, "continue the scene", charBudget: 4_000, CancellationToken.None);

        Assert.Contains(siblings, s => s.Page.Title == "Kazalt the Knight");
        Assert.Contains(siblings, s => s.Page.Title == "Leviathan Bay");
        Assert.DoesNotContain(siblings, s => s.Page.Title == "Kazalt Mirror");
        Assert.DoesNotContain(siblings, s => s.Page.Id == current.Page.Id);
    }

    [Fact]
    public async Task Descendant_folder_pages_are_visible_within_same_book()
    {
        var root = await _wiki.CreateRootAsync("Book", null, CancellationToken.None);
        var characters = await _wiki.CreateFolderAsync(root.Id, "Characters", null, CancellationToken.None);
        var villains = await _wiki.CreateFolderAsync(root.Id, "Villains", characters.Id, CancellationToken.None);

        var current = await _wiki.CreatePageAsync(
            root.Id, characters.Id, "Hero", "# Hero\n\nThe protagonist Remora.", CancellationToken.None);
        await _wiki.CreatePageAsync(
            root.Id, villains.Id, "Antagonist", "# Antagonist\n\nA shadowy figure who hunts Remora across the realm.",
            CancellationToken.None);

        var siblings = await _retriever.RetrieveSiblingsAsync(
            current, "Who is Remora?", charBudget: 4_000, CancellationToken.None);

        Assert.Contains(siblings, s => s.Page.Title == "Antagonist");
    }

    [Fact]
    public async Task Root_level_page_can_see_folder_pages_in_same_book()
    {
        var root = await _wiki.CreateRootAsync("Notes", null, CancellationToken.None);
        var folder = await _wiki.CreateFolderAsync(root.Id, "Drafts", null, CancellationToken.None);

        var current = await _wiki.CreatePageAsync(
            root.Id, null, "Index", "# Index\n\nMain index for project alpha.", CancellationToken.None);
        await _wiki.CreatePageAsync(
            root.Id, null, "Glossary", "# Glossary\n\nProject alpha terms and definitions.",
            CancellationToken.None);
        await _wiki.CreatePageAsync(
            root.Id, folder.Id, "Draft One", "# Draft One\n\nProject alpha draft notes.",
            CancellationToken.None);
        var otherRoot = await _wiki.CreateRootAsync("Other Notes", null, CancellationToken.None);
        await _wiki.CreatePageAsync(
            otherRoot.Id, null, "Alpha Leak", "# Alpha Leak\n\nProject alpha draft glossary from another root.",
            CancellationToken.None);

        var siblings = await _retriever.RetrieveSiblingsAsync(
            current, "alpha project glossary draft", charBudget: 4_000, CancellationToken.None);

        Assert.Contains(siblings, s => s.Page.Title == "Glossary");
        Assert.Contains(siblings, s => s.Page.Title == "Draft One");
        Assert.DoesNotContain(siblings, s => s.Page.Title == "Alpha Leak");
    }

    [Fact]
    public async Task Title_and_heading_hits_outrank_passing_body_mentions()
    {
        var root = await _wiki.CreateRootAsync("Book", null, CancellationToken.None);
        var characters = await _wiki.CreateFolderAsync(root.Id, "Characters", null, CancellationToken.None);

        var current = await _wiki.CreatePageAsync(
            root.Id, characters.Id, "Scene", "# Scene\n\nThe market square.", CancellationToken.None);
        await _wiki.CreatePageAsync(
            root.Id, characters.Id,
            "Merchants Guild",
            "# Merchants Guild\n\nThe market square hosts the guild's weekly auction. Auctions are loud.",
            CancellationToken.None);
        await _wiki.CreatePageAsync(
            root.Id, characters.Id,
            "Random Page",
            "# Random Page\n\nA passing reference to merchants in one sentence among unrelated text. " +
            string.Join(' ', Enumerable.Repeat("filler", 200)),
            CancellationToken.None);

        var siblings = await _retriever.RetrieveSiblingsAsync(
            current, "merchants guild auction", charBudget: 4_000, CancellationToken.None);

        Assert.NotEmpty(siblings);
        Assert.Equal("Merchants Guild", siblings[0].Page.Title);
    }

    [Fact]
    public async Task Budget_is_respected_when_book_context_overflows()
    {
        var root = await _wiki.CreateRootAsync("Book", null, CancellationToken.None);
        var characters = await _wiki.CreateFolderAsync(root.Id, "Characters", null, CancellationToken.None);

        var current = await _wiki.CreatePageAsync(
            root.Id, characters.Id, "Index", "# Index\n\nList of characters.", CancellationToken.None);
        for (var i = 0; i < 10; i++)
        {
            var body = "# Char " + i + "\n\n" + string.Join(' ', Enumerable.Repeat("character description vivid", 400));
            await _wiki.CreatePageAsync(root.Id, characters.Id, "Char " + i, body, CancellationToken.None);
        }

        var siblings = await _retriever.RetrieveSiblingsAsync(
            current, "character description", charBudget: 2_000, CancellationToken.None);

        var totalChars = siblings.Sum(s => s.Snippet.Length);
        Assert.True(totalChars <= 2_000, $"snippet total {totalChars} should fit budget");
        Assert.NotEmpty(siblings);
    }

    [Fact]
    public async Task Empty_query_returns_no_siblings()
    {
        var root = await _wiki.CreateRootAsync("Book", null, CancellationToken.None);
        var current = await _wiki.CreatePageAsync(
            root.Id, null, "The", "# The\n\nAnd for.", CancellationToken.None);
        await _wiki.CreatePageAsync(
            root.Id, null, "Other", "# Other\n\nAlso body.", CancellationToken.None);

        var siblings = await _retriever.RetrieveSiblingsAsync(
            current, "the and for", charBudget: 4_000, CancellationToken.None);

        Assert.Empty(siblings);
    }

    [Fact]
    public async Task Snippet_focuses_on_query_term_window_for_long_pages()
    {
        var root = await _wiki.CreateRootAsync("Book", null, CancellationToken.None);
        var folder = await _wiki.CreateFolderAsync(root.Id, "Chapters", null, CancellationToken.None);

        var current = await _wiki.CreatePageAsync(
            root.Id, folder.Id, "Chapter 1", "# Chapter 1\n\nKazalt rides north.", CancellationToken.None);

        var prefix = string.Join(' ', Enumerable.Repeat("opening filler", 500));
        var hot = "Kazalt confronts the dragon at midnight in the ruined chapel.";
        var suffix = string.Join(' ', Enumerable.Repeat("closing filler", 500));
        await _wiki.CreatePageAsync(
            root.Id, folder.Id, "Chapter 2", "# Chapter 2\n\n" + prefix + " " + hot + " " + suffix,
            CancellationToken.None);

        var siblings = await _retriever.RetrieveSiblingsAsync(
            current, "dragon midnight chapel", charBudget: 4_000, CancellationToken.None);

        var match = siblings.Single(s => s.Page.Title == "Chapter 2");
        Assert.Contains("dragon", match.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chapel", match.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scope_retrieval_ranks_query_matches_and_respects_page_limit()
    {
        var root = await _wiki.CreateRootAsync("Notes", null, CancellationToken.None);
        var distractor = await _wiki.CreatePageAsync(
            root.Id, null, "Archive", "Routine coordination notes.", CancellationToken.None);
        var status = await _wiki.CreatePageAsync(
            root.Id, null, "Launch Status", "Current launch status: GREEN", CancellationToken.None);
        var owner = await _wiki.CreatePageAsync(
            root.Id, null, "Launch Owner", "Current launch owner: Mira", CancellationToken.None);

        var results = _retriever.RetrieveScope(
            [distractor, status, owner],
            "What is the current launch status?",
            charBudget: 1_000,
            maxPages: 1);

        Assert.Single(results);
        Assert.Equal("Launch Status", results[0].Page.Title);
        Assert.Contains("GREEN", results[0].Snippet);
    }

    [Fact]
    public async Task Scope_retrieval_returns_empty_when_no_terms_match()
    {
        var root = await _wiki.CreateRootAsync("Notes", null, CancellationToken.None);
        var page = await _wiki.CreatePageAsync(
            root.Id, null, "Roadmap", "Routine coordination notes.", CancellationToken.None);

        var results = _retriever.RetrieveScope(
            [page],
            "Zephyr access phrase",
            charBudget: 1_000,
            maxPages: 4);

        Assert.Empty(results);
    }
}
