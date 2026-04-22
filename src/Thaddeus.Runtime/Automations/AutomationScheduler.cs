using Microsoft.Extensions.Hosting;

namespace Thaddeus.Runtime.Automations;

/// <summary>
/// Polls the automation store on a steady cadence and fires any automation
/// whose schedule is due. Deliberately simple: we let the store own the
/// canonical <c>NextRunAt</c>, compute "is it time yet?" against UTC, and
/// hand control to <see cref="AutomationRunner"/> for the actual execution.
///
/// A 30-second tick is fast enough that one-minute cron granularity never
/// slips more than ~30 seconds, cheap enough to run forever. The scheduler
/// skips any automation with <c>Enabled=false</c> or <c>Schedule.Kind="off"</c>.
/// </summary>
public sealed class AutomationScheduler : BackgroundService
{
    private readonly IAutomationStore _store;
    private readonly AutomationRunner _runner;
    private readonly ILogger<AutomationScheduler> _logger;
    private readonly TimeSpan _tick = TimeSpan.FromSeconds(30);

    public AutomationScheduler(
        IAutomationStore store,
        AutomationRunner runner,
        ILogger<AutomationScheduler> logger)
    {
        _store = store;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("automation.scheduler.started tick={Tick}", _tick);

        // Short initial delay so the rest of startup (MCP handshake, settings
        // load, etc.) settles before we wake anyone.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "automation.scheduler.tick_failed");
            }

            try { await Task.Delay(_tick, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("automation.scheduler.stopped");
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var items = await _store.ListAsync(ct).ConfigureAwait(false);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (!item.Enabled) continue;
            if (!ScheduleMath.IsDue(item.Schedule, now)) continue;
            if (item.Steps.Count == 0) continue; // don't fire empty automations

            _logger.LogInformation(
                "automation.scheduler.firing id={Id} name={Name} nextRunAt={Next}",
                item.Id, item.Name, item.Schedule!.NextRunAt);

            // Record the fire first — this advances NextRunAt so we don't
            // loop if the run takes longer than one tick.
            await _store.RecordScheduleFiredAsync(item.Id, now, ct).ConfigureAwait(false);

            // Fire-and-forget: the runner runs the steps on its own task.
            // The store has already rolled NextRunAt forward, so the next
            // tick computes from the NEW schedule state.
            try
            {
                _ = await _runner.StartRunAsync(item, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "automation.scheduler.start_failed id={Id}", item.Id);
            }
        }
    }
}
