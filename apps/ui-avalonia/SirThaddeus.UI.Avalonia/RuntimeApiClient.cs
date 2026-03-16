using SirThaddeus.Config;
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

    public async Task<ChatStartResponse> StartRunAsync(
        string prompt,
        CancellationToken cancellationToken,
        string? conversationId = null,
        IReadOnlyList<ChatHistoryMessage>? messages = null)
    {
        var payload = new ChatRequest(
            prompt,
            ConversationId: conversationId,
            SessionId: conversationId,
            Messages: messages);
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

    /// <summary>Clears runtime session-level permission grants (called on "New Chat").</summary>
    public async Task ClearSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsync("/api/session/clear", null, cancellationToken);
            // Fire-and-forget — if the runtime isn't connected, skip silently.
        }
        catch
        {
            // Silently ignore — runtime may not be connected yet.
        }
    }

    public async Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<HealthResponse>("/api/health", JsonOptions, cancellationToken);
    }

    public async Task<AppSettings> SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync("/api/settings", settings, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<AppSettings>(response, "Runtime did not return updated settings.", cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntryDto>> GetAuditAsync(CancellationToken cancellationToken)
    {
        var entries = await _httpClient.GetFromJsonAsync<List<AuditEntryDto>>("/api/audit", JsonOptions, cancellationToken);
        return entries ?? [];
    }

    public async Task<SearchStatusResponse> GetSearchStatusAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<SearchStatusResponse>("/api/search/status", JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Runtime did not return search status.");
    }

    public async Task<MemoryBrowseResponse> GetMemoryAsync(string? filter, int take, CancellationToken cancellationToken)
    {
        var clampedTake = Math.Clamp(take, 1, 200);
        var path = $"/api/memory?take={clampedTake}";
        if (!string.IsNullOrWhiteSpace(filter))
        {
            path += $"&filter={Uri.EscapeDataString(filter.Trim())}";
        }

        using var response = await _httpClient.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<MemoryBrowseResponse>(
            response,
            "Runtime did not return memory data.",
            cancellationToken);
    }

    public async Task<GenericMemoryActionResponse> SaveMemoryFactAsync(string memoryId, SaveMemoryFactRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync($"/api/memory/facts/{Uri.EscapeDataString(memoryId)}", request, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<GenericMemoryActionResponse>(response, "Runtime did not return metadata.", cancellationToken);
    }

    public async Task<GenericMemoryActionResponse> DeleteMemoryFactAsync(string memoryId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.DeleteAsync($"/api/memory/facts/{Uri.EscapeDataString(memoryId)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<GenericMemoryActionResponse>(response, "Runtime did not return metadata.", cancellationToken);
    }

    public async Task<GenericMemoryActionResponse> SaveMemoryEventAsync(string eventId, SaveMemoryEventRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync($"/api/memory/events/{Uri.EscapeDataString(eventId)}", request, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<GenericMemoryActionResponse>(response, "Runtime did not return metadata.", cancellationToken);
    }

    public async Task<GenericMemoryActionResponse> DeleteMemoryEventAsync(string eventId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.DeleteAsync($"/api/memory/events/{Uri.EscapeDataString(eventId)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<GenericMemoryActionResponse>(response, "Runtime did not return metadata.", cancellationToken);
    }

    public async Task<GenericMemoryActionResponse> SaveMemoryChunkAsync(string chunkId, SaveMemoryChunkRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync($"/api/memory/chunks/{Uri.EscapeDataString(chunkId)}", request, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<GenericMemoryActionResponse>(response, "Runtime did not return metadata.", cancellationToken);
    }

    public async Task<GenericMemoryActionResponse> DeleteMemoryChunkAsync(string chunkId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.DeleteAsync($"/api/memory/chunks/{Uri.EscapeDataString(chunkId)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<GenericMemoryActionResponse>(response, "Runtime did not return metadata.", cancellationToken);
    }

    public async Task<GenericMemoryActionResponse> SaveMemoryNuggetAsync(string nuggetId, SaveMemoryNuggetRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync($"/api/memory/nuggets/{Uri.EscapeDataString(nuggetId)}", request, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<GenericMemoryActionResponse>(response, "Runtime did not return metadata.", cancellationToken);
    }

    public async Task<GenericMemoryActionResponse> DeleteMemoryNuggetAsync(string nuggetId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.DeleteAsync($"/api/memory/nuggets/{Uri.EscapeDataString(nuggetId)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<GenericMemoryActionResponse>(response, "Runtime did not return metadata.", cancellationToken);
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

        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<SetActiveProfileResponse>(
            response,
            "Runtime did not return profile update metadata.",
            cancellationToken);
    }

    public async Task<SetActivePersonalityResponse> SetActivePersonalityAsync(string personalityId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/personalities/active",
            new SetActivePersonalityRequest(personalityId),
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<SetActivePersonalityResponse>(
            response,
            "Runtime did not return personality update metadata.",
            cancellationToken);
    }

    public async Task<ProfileDocumentResponse> GetProfileTemplateAsync(string? suggestedProfileId, CancellationToken cancellationToken)
    {
        var path = "/api/profiles/template";
        if (!string.IsNullOrWhiteSpace(suggestedProfileId))
        {
            path += $"?profileId={Uri.EscapeDataString(suggestedProfileId.Trim())}";
        }

        return await _httpClient.GetFromJsonAsync<ProfileDocumentResponse>(path, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Runtime did not return a profile template.");
    }

    public async Task<ProfileDocumentResponse> GetProfileDocumentAsync(string profileId, CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<ProfileDocumentResponse>(
                $"/api/profiles/{Uri.EscapeDataString(profileId)}",
                JsonOptions,
                cancellationToken)
            ?? throw new InvalidOperationException("Runtime did not return a profile document.");
    }

    public async Task<SaveProfileDocumentResponse> CreateProfileAsync(string documentJson, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/profiles",
            new SaveProfileDocumentRequest(documentJson),
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<SaveProfileDocumentResponse>(
            response,
            "Runtime did not return profile save metadata.",
            cancellationToken);
    }

    public async Task<SaveProfileDocumentResponse> UpdateProfileAsync(string profileId, string documentJson, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            $"/api/profiles/{Uri.EscapeDataString(profileId)}",
            new SaveProfileDocumentRequest(documentJson),
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<SaveProfileDocumentResponse>(
            response,
            "Runtime did not return profile save metadata.",
            cancellationToken);
    }

    public async Task<DeleteProfileResponse> DeleteProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.DeleteAsync(
            $"/api/profiles/{Uri.EscapeDataString(profileId)}",
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<DeleteProfileResponse>(
            response,
            "Runtime did not return profile delete metadata.",
            cancellationToken);
    }

    public async Task<PersonalityDocumentResponse> GetPersonalityTemplateAsync(string? suggestedPersonalityId, CancellationToken cancellationToken)
    {
        var path = "/api/personalities/template";
        if (!string.IsNullOrWhiteSpace(suggestedPersonalityId))
        {
            path += $"?personalityId={Uri.EscapeDataString(suggestedPersonalityId.Trim())}";
        }

        return await _httpClient.GetFromJsonAsync<PersonalityDocumentResponse>(path, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Runtime did not return a personality template.");
    }

    public async Task<PersonalityDocumentResponse> GetPersonalityDocumentAsync(string personalityId, CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<PersonalityDocumentResponse>(
                $"/api/personalities/{Uri.EscapeDataString(personalityId)}",
                JsonOptions,
                cancellationToken)
            ?? throw new InvalidOperationException("Runtime did not return a personality document.");
    }

    public async Task<SavePersonalityDocumentResponse> CreatePersonalityAsync(string documentJson, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/personalities",
            new SavePersonalityDocumentRequest(documentJson),
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<SavePersonalityDocumentResponse>(
            response,
            "Runtime did not return personality save metadata.",
            cancellationToken);
    }

    public async Task<SavePersonalityDocumentResponse> UpdatePersonalityAsync(string personalityId, string documentJson, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            $"/api/personalities/{Uri.EscapeDataString(personalityId)}",
            new SavePersonalityDocumentRequest(documentJson),
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<SavePersonalityDocumentResponse>(
            response,
            "Runtime did not return personality save metadata.",
            cancellationToken);
    }

    public async Task<DeletePersonalityResponse> DeletePersonalityAsync(string personalityId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.DeleteAsync(
            $"/api/personalities/{Uri.EscapeDataString(personalityId)}",
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredJsonAsync<DeletePersonalityResponse>(
            response,
            "Runtime did not return personality delete metadata.",
            cancellationToken);
    }

    public async Task<bool> SubmitPermissionDecisionAsync(
        string requestId,
        bool approved,
        bool rememberForSession,
        bool persistAsAlways,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/permissions/{Uri.EscapeDataString(requestId)}/decision",
            new PermissionDecisionRequest(approved, rememberForSession, persistAsAlways),
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

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException(UnwrapErrorBody(body));
        }

        response.EnsureSuccessStatusCode();
    }

    private static async Task<T> ReadRequiredJsonAsync<T>(
        HttpResponseMessage response,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken))
            ?? throw new InvalidOperationException(errorMessage);
    }

    private static string UnwrapErrorBody(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length >= 2 &&
            trimmed[0] == '"' &&
            trimmed[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(trimmed) ?? trimmed;
            }
            catch
            {
                return trimmed;
            }
        }

        return trimmed;
    }
}


