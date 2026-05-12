using SirThaddeus.AuditLog;
using Thaddeus.Runtime.State;
using Thaddeus.Runtime.Tools;
using Thaddeus.Runtime.Voice;

namespace Thaddeus.Runtime.Hosting;

public sealed class RuntimeStopAllService
{
    private readonly VoiceModeController _voice;
    private readonly VoiceHostProcessSupervisor _voiceHost;
    private readonly McpClientHost _mcp;
    private readonly RuntimeStateMachine _stateMachine;
    private readonly IAuditLogger _audit;
    private readonly ILogger<RuntimeStopAllService> _logger;

    public RuntimeStopAllService(
        VoiceModeController voice,
        VoiceHostProcessSupervisor voiceHost,
        McpClientHost mcp,
        RuntimeStateMachine stateMachine,
        IAuditLogger audit,
        ILogger<RuntimeStopAllService> logger)
    {
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
        _voiceHost = voiceHost ?? throw new ArgumentNullException(nameof(voiceHost));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RuntimeStopAllResult> StopAllAsync(CancellationToken cancellationToken = default)
    {
        var stopped = new List<string>();
        var errors = new List<string>();

        try
        {
            _voice.StopAll();
            stopped.Add("voice operations");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "runtime.stop_all.voice_failed");
            errors.Add($"voice operations: {ex.Message}");
        }

        try
        {
            if (_voiceHost.StopHost())
            {
                stopped.Add("VoiceHost");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "runtime.stop_all.voicehost_failed");
            errors.Add($"VoiceHost: {ex.Message}");
        }

        try
        {
            if (await _mcp.StopChildAsync(cancellationToken).ConfigureAwait(false))
            {
                stopped.Add("MCP server");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "runtime.stop_all.mcp_failed");
            errors.Add($"MCP server: {ex.Message}");
        }

        var result = new RuntimeStopAllResult(
            Applied: true,
            Current: _stateMachine.Current.ToString(),
            Stopped: stopped,
            Errors: errors);

        _audit.Append(new AuditEvent
        {
            Actor = "user",
            Action = "RUNTIME_STOP_ALL",
            Target = "managed-processes",
            Result = errors.Count == 0 ? "ok" : "partial",
            Details = new Dictionary<string, object>
            {
                ["stopped"] = stopped.ToArray(),
                ["errors"] = errors.ToArray(),
                ["currentState"] = result.Current,
            }
        });

        return result;
    }
}

public sealed record RuntimeStopAllResult(
    bool Applied,
    string Current,
    IReadOnlyList<string> Stopped,
    IReadOnlyList<string> Errors);
