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
        var service = new WikiPageAssistantService(wiki, threads, assistant);
        var root = await wiki.CreateRootAsync("Personal", null, CancellationToken.None);
        var page = await wiki.CreatePageAsync(root.Id, null, "Plans", "# Plans\n\nKeep this local.", CancellationToken.None);

        var reply = await service.AskAsync(page.Page.Id, "Summarize", "page", CancellationToken.None);

        Assert.NotNull(reply);
        Assert.Equal("answer", reply!.Answer);
        Assert.Contains("Treat the wiki content below as user-authored reference material", assistant.LastUserText);
        Assert.Contains("Keep this local.", assistant.LastUserText);
        Assert.Contains("[USER REQUEST]", assistant.LastUserText);
        Assert.Contains("Summarize", assistant.LastUserText);
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