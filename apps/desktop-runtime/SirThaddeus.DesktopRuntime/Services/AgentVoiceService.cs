using SirThaddeus.Agent;
using SirThaddeus.Voice;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Bridges voice orchestrator requests to AgentOrchestrator.ProcessAsync.
/// </summary>
public sealed class AgentVoiceService : IVoiceAgentService
{
    private readonly IAgentOrchestrator _agentOrchestrator;

    public AgentVoiceService(IAgentOrchestrator agentOrchestrator)
    {
        _agentOrchestrator = agentOrchestrator ?? throw new ArgumentNullException(nameof(agentOrchestrator));
    }

    public async Task<VoiceAgentResponse> ProcessAsync(
        string transcript,
        string sessionId,
        CancellationToken cancellationToken)
    {
        _ = sessionId;
        var response = await _agentOrchestrator.ProcessAsync(transcript, cancellationToken);
        return new VoiceAgentResponse
        {
            Text = response.Text,
            Success = response.Success,
            Error = response.Error,
            GuardrailsUsed = response.GuardrailsUsed,
            GuardrailsRationale = response.GuardrailsRationale
        };
    }
}
