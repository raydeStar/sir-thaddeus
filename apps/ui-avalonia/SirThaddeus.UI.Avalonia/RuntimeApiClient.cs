using SirThaddeus.Contracts;
using System.Net.Http.Json;
using System.Text.Json;

namespace SirThaddeus.UI.Avalonia;

internal sealed class RuntimeApiClient
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public RuntimeApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ChatStartResponse> StartRunAsync(string prompt, CancellationToken cancellationToken)
    {
        var payload = new ChatRequest(prompt);
        using var response = await _httpClient.PostAsJsonAsync("/api/chat", payload, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatStartResponse>(JsonOptions, cancellationToken))
            ?? throw new InvalidOperationException("Runtime did not return run metadata.");
    }

    public async Task<bool> CancelRunAsync(string runId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/runs/{Uri.EscapeDataString(runId)}/cancel",
            new CancelRunRequest("user_stop"),
            JsonOptions,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await response.Content.ReadFromJsonAsync<CancelRunResponse>(JsonOptions, cancellationToken);
        return body?.Accepted == true;
    }

    public async Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<HealthResponse>("/api/health", JsonOptions, cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntryDto>> GetAuditAsync(CancellationToken cancellationToken)
    {
        var entries = await _httpClient.GetFromJsonAsync<List<AuditEntryDto>>("/api/audit", JsonOptions, cancellationToken);
        return entries ?? [];
    }
}
