using SirThaddeus.LlmClient;

namespace Thaddeus.Runtime.Chat;

public sealed class LlmRuntimeRegistry
{
    private readonly object _gate = new();
    private ILlmRuntimeDiagnostics? _primaryDiagnostics;
    private ILlmUsageTelemetry? _primaryUsage;
    private LlmRuntimeHealthSnapshot? _startupSnapshot;

    public void SetPrimary(ILlmRuntimeDiagnostics diagnostics)
    {
        lock (_gate)
        {
            _primaryDiagnostics = diagnostics;
            _primaryUsage = diagnostics as ILlmUsageTelemetry;
        }
    }

    public void SetStartupSnapshot(LlmRuntimeHealthSnapshot snapshot)
    {
        lock (_gate)
            _startupSnapshot = snapshot;
    }

    public LlmRuntimeHealthSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _primaryDiagnostics?.GetRuntimeHealthSnapshot()
                   ?? _startupSnapshot
                   ?? new LlmRuntimeHealthSnapshot();
        }
    }

    public LlmUsageSnapshot GetUsageSnapshot()
    {
        lock (_gate)
            return _primaryUsage?.GetUsageSnapshot() ?? new LlmUsageSnapshot();
    }
}
