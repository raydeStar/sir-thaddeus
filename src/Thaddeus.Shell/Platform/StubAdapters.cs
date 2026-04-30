namespace Thaddeus.Shell.Platform;

/// <summary>
/// Phase-1 stub adapters. They report <c>IsSupported = false</c> and log when called.
/// Real platform implementations land in later phases (per spec §17 they live under
/// <c>Platform/Windows</c>, <c>Platform/MacOS</c>, <c>Platform/Linux</c>).
/// </summary>
public sealed class StubTrayAdapter : ITrayAdapter
{
    private readonly ILogger<StubTrayAdapter> _logger;

    /// <summary>Initialises the stub.</summary>
    public StubTrayAdapter(ILogger<StubTrayAdapter> logger) { _logger = logger; }

    /// <inheritdoc/>
    public bool IsSupported => false;

    /// <inheritdoc/>
    public Task InitializeAsync(TrayMenu menu, CancellationToken ct)
    {
        _logger.LogInformation("tray.stub initialise items={Count}", menu.Items.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Phase-1 stub global shortcut adapter.</summary>
public sealed class StubGlobalShortcutAdapter : IGlobalShortcutAdapter
{
    private readonly ILogger<StubGlobalShortcutAdapter> _logger;

    /// <summary>Initialises the stub.</summary>
    public StubGlobalShortcutAdapter(ILogger<StubGlobalShortcutAdapter> logger) { _logger = logger; }

    /// <inheritdoc/>
    public bool IsSupported => false;

    /// <inheritdoc/>
    public Task<bool> RegisterAsync(string id, KeyChord chord, CancellationToken ct)
    {
        _logger.LogInformation("shortcut.stub register id={Id} chord={Chord}", id, chord);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public Task UnregisterAsync(string id)
    {
        _logger.LogInformation("shortcut.stub unregister id={Id}", id);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public event EventHandler<string>? Triggered { add { } remove { } }

    /// <inheritdoc/>
    public event EventHandler<string>? Released { add { } remove { } }

    /// <inheritdoc/>
    public void Dispose() { }
}
