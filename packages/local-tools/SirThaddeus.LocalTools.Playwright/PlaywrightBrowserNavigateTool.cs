using Microsoft.Playwright;
using SirThaddeus.PermissionBroker;
using SirThaddeus.ToolRunner;

namespace SirThaddeus.LocalTools.Playwright;

/// <summary>
/// Playwright-backed browser navigation tool.
/// Launches a real browser, navigates to the specified URL, and returns page info.
/// </summary>
public sealed class PlaywrightBrowserNavigateTool : ITool, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    /// <inheritdoc />
    public string Name => "browser_navigate";

    /// <inheritdoc />
    public string Description => "Navigates a browser to the specified URL using Playwright.";

    /// <inheritdoc />
    public Capability RequiredCapability => Capability.BrowserControl;

    /// <inheritdoc />
    public async Task<object?> ExecuteAsync(ToolExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        // ─────────────────────────────────────────────────────────────────
        // Extract and validate URL argument
        // ─────────────────────────────────────────────────────────────────

        var urlString = context.Call.Arguments?.TryGetValue("url", out var urlObj) == true
            ? urlObj?.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(urlString))
        {
            return new BrowserNavigateResult
            {
                Success = false,
                Error = "Missing required 'url' argument."
            };
        }

        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
        {
            return new BrowserNavigateResult
            {
                Success = false,
                Error = $"Invalid URL format: {urlString}"
            };
        }

        // ─────────────────────────────────────────────────────────────────
        // Enforce domain scope
        // ─────────────────────────────────────────────────────────────────

        context.EnforceUrlScope(uri);

        // ─────────────────────────────────────────────────────────────────
        // Navigate using Playwright
        // ─────────────────────────────────────────────────────────────────

        await _lock.WaitAsync(context.CancellationToken);
        try
        {
            await EnsureBrowserAsync();

            var page = await _browser!.NewPageAsync();
            try
            {
                var response = await page.GotoAsync(uri.ToString(), new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000 // 30 seconds
                });

                context.CancellationToken.ThrowIfCancellationRequested();

                var title = await page.TitleAsync();
                var finalUrl = page.Url;

                // Extract a small text excerpt from the page body
                var excerpt = await ExtractExcerptAsync(page);

                return new BrowserNavigateResult
                {
                    Success = true,
                    FinalUrl = finalUrl,
                    Title = title,
                    Excerpt = excerpt,
                    StatusCode = response?.Status
                };
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (TimeoutException ex)
        {
            return new BrowserNavigateResult
            {
                Success = false,
                Error = $"Navigation timed out: {ex.Message}"
            };
        }
        catch (PlaywrightException ex)
        {
            return new BrowserNavigateResult
            {
                Success = false,
                Error = $"Browser error: {ex.Message}"
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Browser Lifecycle
    // ─────────────────────────────────────────────────────────────────────

    private async Task EnsureBrowserAsync()
    {
        if (_browser is { IsConnected: true })
            return;

        _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_browser != null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;

        _lock.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static async Task<string?> ExtractExcerptAsync(IPage page)
    {
        try
        {
            // Attempt to get first 500 chars of visible text
            var text = await page.EvaluateAsync<string>("""
                () => {
                    const body = document.body;
                    if (!body) return '';
                    return body.innerText.substring(0, 500).trim();
                }
                """);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Result of a browser navigation operation.
/// </summary>
public sealed record BrowserNavigateResult
{
    /// <summary>
    /// Whether the navigation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The final URL after any redirects.
    /// </summary>
    public string? FinalUrl { get; init; }

    /// <summary>
    /// The page title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// A short excerpt of the page text content.
    /// </summary>
    public string? Excerpt { get; init; }

    /// <summary>
    /// HTTP status code if available.
    /// </summary>
    public int? StatusCode { get; init; }

    /// <summary>
    /// Error message if navigation failed.
    /// </summary>
    public string? Error { get; init; }
}
