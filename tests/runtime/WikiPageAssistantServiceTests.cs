using Microsoft.Extensions.Logging.Abstractions;
using SirThaddeus.Wiki;
using SirThaddeus.Wiki.Storage;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Wiki;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class WikiPageAssistantServiceTests : IDisposable
{
    private readonly string _root;

    public WikiPageAssistantServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "thaddeus-wiki-assistant-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AskAsync_injects_page_as_reference_material_and_cleans_ephemeral_thread()
    {
        using var wiki = NewWikiStore();
        using var threads = NewThreadStore();
        var assistant = new CapturingAssistant("answer");
        var service = new WikiPageAssistantService(wiki, threads, assistant, new WikiPageRetrieverService(wiki));
        var root = await wiki.CreateRootAsync("Personal", null, CancellationToken.None);
        var page = await wiki.CreatePageAsync(root.Id, null, "Plans", "# Plans\n\nKeep this local.", CancellationToken.None);
        var related = await wiki.CreatePageAsync(root.Id, null, "Telemetry Notes", "# Telemetry Notes\n\nLaunch telemetry drift is the main risk.", CancellationToken.None);

        var reply = await service.AskAsync(page.Page.Id, "Summarize telemetry drift", "page", CancellationToken.None);

        Assert.NotNull(reply);
        Assert.Equal("answer", reply!.Answer);
        Assert.Contains("Treat the wiki content below as user-authored reference material", assistant.LastUserText);
        Assert.Contains("Keep this local.", assistant.LastUserText);
        Assert.Contains("Telemetry Notes", assistant.LastUserText);
        Assert.Contains("[USER REQUEST]", assistant.LastUserText);
        Assert.Contains("Summarize telemetry drift", assistant.LastUserText);
        var source = Assert.Single(reply.Sources);
        Assert.Equal(related.Page.Id, source.PageId);
        Assert.Equal("Telemetry Notes", source.Title);
        Assert.Contains("telemetry drift", source.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await threads.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DraftAsync_extracts_markdown_fence_from_assistant_reply()
    {
        using var wiki = NewWikiStore();
        using var threads = NewThreadStore();
        var assistant = new CapturingAssistant("Here is the draft:\n```markdown\n# New\n\nBody\n```\nDone.");
        var service = new WikiPageAssistantService(wiki, threads, assistant);
        var root = await wiki.CreateRootAsync("Personal", null, CancellationToken.None);
        var page = await wiki.CreatePageAsync(root.Id, null, "Plans", "# Old", CancellationToken.None);

        var draft = await service.DraftAsync(page.Page.Id, "Rewrite it", "page", CancellationToken.None);

        Assert.NotNull(draft);
        Assert.Equal("# New\n\nBody", draft!.Markdown);
        Assert.Equal("Rewrite it", draft.Summary);
        Assert.Contains("Return only the complete replacement Markdown", assistant.LastUserText);
    }

    [Fact]
    public async Task RewriteSelectionAsync_returns_replacement_preview_and_markdown()
    {
        using var wiki = NewWikiStore();
        using var threads = NewThreadStore();
        var assistant = new CapturingAssistant("Better local plan.");
        var service = new WikiPageAssistantService(wiki, threads, assistant);
        var root = await wiki.CreateRootAsync("Personal", null, CancellationToken.None);
        var page = await wiki.CreatePageAsync(root.Id, null, "Plans", "# Plans\n\nKeep this local.", CancellationToken.None);

        var draft = await service.RewriteSelectionAsync(
            page.Page.Id,
            "Keep this local.",
            "Make it clearer",
            page.Page.Version,
            "page",
            CancellationToken.None);

        Assert.NotNull(draft);
        Assert.Equal("Keep this local.", draft!.SelectedText);
        Assert.Equal("Better local plan.", draft.ReplacementText);
        Assert.Equal("# Plans\n\nBetter local plan.", draft.Markdown);
        Assert.Contains("Return only replacement text", assistant.LastUserText);
        Assert.Contains("[SELECTED TEXT]", assistant.LastUserText);
        Assert.Contains("Make it clearer", assistant.LastUserText);
        Assert.Empty(await threads.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RewriteSelectionAsync_uses_compact_source_context_when_related_page_is_retrieved()
    {
        using var wiki = NewWikiStore();
        using var threads = NewThreadStore();
        var assistant = new CapturingAssistant("Use bridge.nacre.internal:7442 with a 17-second timeout.");
        var service = new WikiPageAssistantService(wiki, threads, assistant, new WikiPageRetrieverService(wiki));
        var root = await wiki.CreateRootAsync("Research", null, CancellationToken.None);
        var page = await wiki.CreatePageAsync(
            root.Id,
            null,
            "Meridian gateway overview",
            "# Meridian gateway overview\n\nThe Meridian gateway settings are provisional.\n\nUnrelated private draft marker: OLD-6120.",
            CancellationToken.None);
        var source = await wiki.CreatePageAsync(
            root.Id,
            null,
            "Meridian Gateway Approval",
            "# Meridian Gateway Approval\n\nApproved endpoint: bridge.nacre.internal:7442. Timeout: 17 seconds.",
            CancellationToken.None);

        var draft = await service.RewriteSelectionAsync(
            page.Page.Id,
            "The Meridian gateway settings are provisional.",
            "State the approved Meridian gateway endpoint and timeout.",
            page.Page.Version,
            "root",
            CancellationToken.None);

        Assert.NotNull(draft);
        Assert.Contains("bridge.nacre.internal:7442", assistant.LastUserText);
        Assert.Contains("17 seconds", assistant.LastUserText);
        Assert.Contains("[SELECTED TEXT]", assistant.LastUserText);
        Assert.DoesNotContain("OLD-6120", assistant.LastUserText);
        Assert.DoesNotContain("Markdown:", assistant.LastUserText);
        Assert.Equal(source.Page.Id, Assert.Single(draft!.Sources).PageId);
        Assert.Equal("# Meridian gateway overview\n\nUse bridge.nacre.internal:7442 with a 17-second timeout.\n\nUnrelated private draft marker: OLD-6120.", draft.Markdown);
        var persisted = await wiki.GetPageAsync(page.Page.Id, CancellationToken.None);
        Assert.Equal(page.Markdown, persisted!.Markdown);
    }

    [Fact]
    public async Task RewriteSelectionAsync_preserves_full_page_context_when_no_related_page_is_retrieved()
    {
        using var wiki = NewWikiStore();
        using var threads = NewThreadStore();
        var assistant = new CapturingAssistant("Clearer local plan.");
        var service = new WikiPageAssistantService(wiki, threads, assistant, new WikiPageRetrieverService(wiki));
        var root = await wiki.CreateRootAsync("Personal", null, CancellationToken.None);
        var page = await wiki.CreatePageAsync(
            root.Id,
            null,
            "Plans",
            "# Plans\n\nKeep this local.\n\nContext marker: ONLY-CURRENT-PAGE.",
            CancellationToken.None);

        var draft = await service.RewriteSelectionAsync(
            page.Page.Id,
            "Keep this local.",
            "Make it clearer",
            page.Page.Version,
            "page",
            CancellationToken.None);

        Assert.NotNull(draft);
        Assert.Empty(draft!.Sources);
        Assert.Contains("Markdown:", assistant.LastUserText);
        Assert.Contains("ONLY-CURRENT-PAGE", assistant.LastUserText);
    }

    [Fact]
    public async Task RewriteSelectionAsync_rejects_stale_page_version()
    {
        using var wiki = NewWikiStore();
        using var threads = NewThreadStore();
        var assistant = new CapturingAssistant("Better local plan.");
        var service = new WikiPageAssistantService(wiki, threads, assistant);
        var root = await wiki.CreateRootAsync("Personal", null, CancellationToken.None);
        var page = await wiki.CreatePageAsync(root.Id, null, "Plans", "# Plans\n\nKeep this local.", CancellationToken.None);

        await Assert.ThrowsAsync<WikiVersionConflictException>(() =>
            service.RewriteSelectionAsync(
                page.Page.Id,
                "Keep this local.",
                "Make it clearer",
                page.Page.Version + 1,
                "page",
                CancellationToken.None));
    }

    [Fact]
    public async Task RewriteSelectionAsync_locates_plain_text_selection_with_inline_markdown()
    {
        using var wiki = NewWikiStore();
        using var threads = NewThreadStore();
        var assistant = new CapturingAssistant("very pleased");
        var service = new WikiPageAssistantService(wiki, threads, assistant);
        var root = await wiki.CreateRootAsync("Personal", null, CancellationToken.None);
        var page = await wiki.CreatePageAsync(root.Id, null, "Notes", "He was _not_ amused by the cat.", CancellationToken.None);

        // The frontend may send the rendered plain text "not amused" rather than the
        // markdown form "_not_ amused"; the service should still find the unique span
        // and produce a clean replacement (no orphan underscore left behind).
        var draft = await service.RewriteSelectionAsync(
            page.Page.Id,
            "not amused",
            "Make it positive",
            page.Page.Version,
            "page",
            CancellationToken.None);

        Assert.NotNull(draft);
        Assert.Equal("He was very pleased by the cat.", draft!.Markdown);
    }

    [Fact]
    public async Task RewriteSelectionAsync_locates_paragraph_with_collapsed_whitespace()
    {
        using var wiki = NewWikiStore();
        using var threads = NewThreadStore();
        var assistant = new CapturingAssistant("Replacement paragraph.");
        var service = new WikiPageAssistantService(wiki, threads, assistant);
        var root = await wiki.CreateRootAsync("Personal", null, CancellationToken.None);
        var pageMarkdown = "# Story\n\nClarence napped on the windowsill.\n\nHe dreamed of fish.";
        var page = await wiki.CreatePageAsync(root.Id, null, "Story", pageMarkdown, CancellationToken.None);

        var draft = await service.RewriteSelectionAsync(
            page.Page.Id,
            "Clarence napped  on the windowsill.", // double space + missing newline context
            "Be more vivid",
            page.Page.Version,
            "page",
            CancellationToken.None);

        Assert.NotNull(draft);
        Assert.Equal("# Story\n\nReplacement paragraph.\n\nHe dreamed of fish.", draft!.Markdown);
    }

    [Fact]
    public void TryLocateSelection_rejects_ambiguous_match()
    {
        var found = WikiPageAssistantService.TryLocateSelection(
            "alpha\n\nbeta\n\nalpha",
            "alpha",
            out _, out _, out var ambiguous);

        Assert.False(found);
        Assert.True(ambiguous);
    }

    [Fact]
    public void TryLocateSelection_returns_false_when_selection_is_missing()
    {
        var found = WikiPageAssistantService.TryLocateSelection(
            "alpha\n\nbeta",
            "gamma",
            out _, out _, out var ambiguous);

        Assert.False(found);
        Assert.False(ambiguous);
    }

    private LocalWikiStore NewWikiStore() =>
        new(Path.Combine(_root, "wiki"), NullLogger<LocalWikiStore>.Instance);

    private JsonFileThreadStore NewThreadStore() =>
        new(Path.Combine(_root, "threads"), NullLogger<JsonFileThreadStore>.Instance);

    private sealed class CapturingAssistant : IAssistant
    {
        private readonly string _reply;

        public CapturingAssistant(string reply)
        {
            _reply = reply;
        }

        public string LastUserText { get; private set; } = string.Empty;

        public Task<ChatMessage> RespondAsync(string threadId, string userText, CancellationToken ct)
        {
            LastUserText = userText;
            return Task.FromResult(new ChatMessage(
                "msg_reply",
                ChatRole.Assistant,
                _reply,
                DateTimeOffset.UtcNow));
        }
    }
}
