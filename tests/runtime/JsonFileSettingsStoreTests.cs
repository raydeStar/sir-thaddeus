using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Settings;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class JsonFileSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;

    public JsonFileSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "thaddeus-settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task GetAsync_returns_defaults_when_file_missing()
    {
        var store = NewStore();
        var doc = await store.GetAsync(CancellationToken.None);

        var defaults = SettingsDocument.Defaults();
        Assert.Equal(defaults, doc);
    }

    [Fact]
    public async Task ReplaceAsync_persists_and_round_trips()
    {
        var store = NewStore();
        var updated = SettingsDocument.Defaults() with
        {
            Llm = new LlmSettings("openai", "gpt-4o-mini", "https://api.openai.com", "sk-test"),
            Privacy = new PrivacySettings(true, true, false),
        };

        await store.ReplaceAsync(updated, CancellationToken.None);

        // New store reads the same path and sees the changes.
        var fresh = NewStore();
        var roundTripped = await fresh.GetAsync(CancellationToken.None);
        Assert.Equal(updated, roundTripped);
    }

    [Fact]
    public async Task ReplaceAsync_raises_changed_event()
    {
        var store = NewStore();
        SettingsDocument? observed = null;
        store.Changed += d => observed = d;

        var updated = SettingsDocument.Defaults() with
        {
            Shortcuts = new ShortcutSettings("Ctrl+Alt+Space", "Ctrl+Alt+Esc"),
        };
        await store.ReplaceAsync(updated, CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(updated.Shortcuts, observed!.Shortcuts);
    }

    [Fact]
    public async Task GetAsync_returns_defaults_on_corrupt_file()
    {
        var path = Path.Combine(_tempDir, "runtime-settings.json");
        await File.WriteAllTextAsync(path, "{ this isn't valid json");
        var store = new JsonFileSettingsStore(path, NullLogger<JsonFileSettingsStore>.Instance);

        var doc = await store.GetAsync(CancellationToken.None);

        Assert.Equal(SettingsDocument.Defaults(), doc);
    }

    private JsonFileSettingsStore NewStore() =>
        new(Path.Combine(_tempDir, "runtime-settings.json"), NullLogger<JsonFileSettingsStore>.Instance);
}
