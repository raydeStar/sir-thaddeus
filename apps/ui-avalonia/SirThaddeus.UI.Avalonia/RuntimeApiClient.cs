using SirThaddeus.Contracts;
using System.Collections.Generic;
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

    public async Task<MemoryBrowseResponse> GetMemoryAsync(string? filter, int take, CancellationToken cancellationToken)
    {
        var clampedTake = Math.Clamp(take, 1, 200);
        var path = $"/api/memory?take={clampedTake}";
        if (!string.IsNullOrWhiteSpace(filter))
        {
            path += $"&filter={Uri.EscapeDataString(filter.Trim())}";
        }

        return await _httpClient.GetFromJsonAsync<MemoryBrowseResponse>(path, JsonOptions, cancellationToken)
            ?? new MemoryBrowseResponse([], [], [], [], 0, 0, 0, 0);
    }

    public async Task<ProfileSummaryResponse> GetProfilesAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<ProfileSummaryResponse>("/api/profiles", JsonOptions, cancellationToken)
            ?? new ProfileSummaryResponse(null, [], [], "");
    }

    public async Task<SetActiveProfileResponse> SetActiveProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/profiles/active",
            new SetActiveProfileRequest(profileId),
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SetActiveProfileResponse>(JsonOptions, cancellationToken))
            ?? throw new InvalidOperationException("Runtime did not return profile update metadata.");
    }

    public async Task<SetActivePersonalityResponse> SetActivePersonalityAsync(string personalityId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/personalities/active",
            new SetActivePersonalityRequest(personalityId),
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SetActivePersonalityResponse>(JsonOptions, cancellationToken))
            ?? throw new InvalidOperationException("Runtime did not return personality update metadata.");
    }

    public async Task<bool> SubmitPermissionDecisionAsync(string requestId, bool approved, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/permissions/{Uri.EscapeDataString(requestId)}/decision",
            new PermissionDecisionRequest(approved),
            JsonOptions,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await response.Content.ReadFromJsonAsync<PermissionDecisionResponse>(JsonOptions, cancellationToken);
        return body?.Applied == true;
    }

    public async IAsyncEnumerable<RuntimeEventEnvelope> StreamRunEventsAsync(
        string runId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{Uri.EscapeDataString(runId)}/events");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line["data:".Length..].Trim();
            if (json.Length == 0)
            {
                continue;
            }

            var envelope = JsonSerializer.Deserialize<RuntimeEventEnvelope>(json, JsonOptions);
            if (envelope is not null)
            {
                yield return envelope;
            }
        }
    }
}
