using Microsoft.Extensions.Hosting;
using SirThaddeus.LlmClient;
using Thaddeus.Runtime.Settings;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Chat;

public sealed class LmStudioWarmupHostedService : BackgroundService
{
    private readonly ISettingsStore _settings;
    private readonly LlmRuntimeRegistry _registry;
    private readonly ILogger<LmStudioWarmupHostedService> _logger;

    public LmStudioWarmupHostedService(
        ISettingsStore settings,
        LlmRuntimeRegistry registry,
        ILogger<LmStudioWarmupHostedService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SettingsDocument doc;
        try
        {
            doc = await _settings.GetAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "llm.warmup_settings_failed");
            return;
        }

        if (ShouldSkip(doc.Llm))
        {
            _logger.LogInformation("llm.warmup_skipped provider={Provider}", doc.Llm.Provider);
            return;
        }

        var options = AssistantRouter.ToClientOptions(doc.Llm);
        using var client = new LmStudioClient(options, logger: _loggerFactoryShim);
        var result = await client.WarmupAsync(stoppingToken).ConfigureAwait(false);
        _registry.SetStartupSnapshot(result.Snapshot);

        if (!result.Completed)
        {
            _logger.LogWarning(
                "llm.warmup_degraded model={Model} reachable={Reachable} error={Error}",
                options.Model,
                result.Reachable,
                result.Error);
            return;
        }

        _logger.LogInformation("llm.warmup_ready model={Model}", options.Model);

        if (!options.EnableKeepWarm || options.KeepWarmIntervalMinutes <= 0)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.KeepWarmIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var keepWarm = await client.WarmupAsync(stoppingToken).ConfigureAwait(false);
            _registry.SetStartupSnapshot(keepWarm.Snapshot);
        }
    }

    private static bool ShouldSkip(LlmSettings llm)
        => string.Equals(llm.Provider, "stub", StringComparison.OrdinalIgnoreCase)
           || string.IsNullOrWhiteSpace(llm.BaseUrl)
           || string.IsNullOrWhiteSpace(llm.ModelId);

    private static readonly Microsoft.Extensions.Logging.ILogger<LmStudioClient> _loggerFactoryShim =
        Microsoft.Extensions.Logging.Abstractions.NullLogger<LmStudioClient>.Instance;
}
