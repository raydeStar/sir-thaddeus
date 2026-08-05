using System.Diagnostics;
using System.Text.Json;
using SirThaddeus.LlmClient;
using Thaddeus.SharedTypes;
using LlmChatMessage = SirThaddeus.LlmClient.ChatMessage;

namespace Thaddeus.Runtime.Chat;

public sealed partial class ModelCapabilityCertificationService
{
    private sealed record ForcedToolTransportProbe(
        string Id,
        ToolDefinition Tool,
        string UserPrompt,
        IReadOnlyDictionary<string, object> ExpectedArguments);

    private static readonly IReadOnlyList<ForcedToolTransportProbe> ForcedToolTransportProbes =
    [
        new(
            "single_string",
            BuildSyntheticTool(
                "inspect_route_manifest",
                "Read one synthetic route manifest without executing any real tool.",
                new Dictionary<string, object>
                {
                    ["route"] = new { type = "string" },
                },
                ["route"]),
            "Call inspect_route_manifest with route set to north-17.",
            new Dictionary<string, object> { ["route"] = "north-17" }),
        new(
            "mixed_arguments",
            BuildSyntheticTool(
                "query_station_window",
                "Read one synthetic station window without executing any real tool.",
                new Dictionary<string, object>
                {
                    ["station"] = new { type = "string" },
                    ["hours"] = new { type = "integer", minimum = 1, maximum = 24 },
                },
                ["station", "hours"]),
            "Use query_station_window for station Mesa-9 and the next 6 hours.",
            new Dictionary<string, object> { ["station"] = "Mesa-9", ["hours"] = 6 }),
    ];

    public async Task<ModelCapabilityStatus> GetForcedToolTransportStatusAsync(
        CancellationToken cancellationToken)
    {
        var document = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        return BuildForcedToolTransportStatus(document, _runtime.GetSnapshot());
    }

    public async Task<ModelCapabilityStatus> RetestForcedToolTransportAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RetestTimeout);
        var ct = timeout.Token;
        var stopwatch = Stopwatch.StartNew();
        var document = await _settings.GetAsync(ct).ConfigureAwait(false);
        var probeSettings = document.Llm with
        {
            Temperature = 0.0,
            MaxTokens = RetestMaxOutputTokens,
        };
        ILlmClient? requiredClient = null;
        ILlmClient? autoClient = null;
        var calls = 0;
        string? reportedModel = null;
        string? selectedMode = null;
        var results = new List<ModelCapabilityProbeResult>();

        try
        {
            requiredClient = _clientFactory(probeSettings, ForcedToolChoiceMode.Required);
            reportedModel = string.Equals(document.Llm.ModelId, "auto", StringComparison.OrdinalIgnoreCase)
                ? await requiredClient.GetModelNameAsync(ct).ConfigureAwait(false)
                : document.Llm.ModelId;

            foreach (var probe in ForcedToolTransportProbes)
            {
                calls++;
                var response = await InvokeForcedToolTransportProbeAsync(requiredClient, probe, ct)
                    .ConfigureAwait(false);
                results.Add(GradeForcedToolTransportProbe("required", probe, response));
            }

            if (results.All(result => result.Passed))
            {
                selectedMode = "required";
            }
            else
            {
                autoClient = _clientFactory(probeSettings, ForcedToolChoiceMode.Auto);
                var autoResults = new List<ModelCapabilityProbeResult>(ForcedToolTransportProbes.Count);
                foreach (var probe in ForcedToolTransportProbes)
                {
                    calls++;
                    var response = await InvokeForcedToolTransportProbeAsync(autoClient, probe, ct)
                        .ConfigureAwait(false);
                    autoResults.Add(GradeForcedToolTransportProbe("auto", probe, response));
                }
                results.AddRange(autoResults);
                if (autoResults.All(result => result.Passed)) selectedMode = "auto";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "model_capability.forced_tool_transport_retest_failed calls={Calls}", calls);
            results.Add(new ModelCapabilityProbeResult("retest_error", false, SafeReason(ex)));
        }
        finally
        {
            if (requiredClient is IDisposable requiredDisposable) requiredDisposable.Dispose();
            if (autoClient is IDisposable autoDisposable) autoDisposable.Dispose();
        }

        stopwatch.Stop();
        var status = results.Any(result => result.Id == "retest_error")
            ? "error"
            : selectedMode is not null
                ? "certified"
                : "unsupported";
        var certificate = new ModelCapabilityCertificate(
            ModelCapabilityPolicy.ForcedToolTransportCapability,
            status,
            ModelCapabilityPolicy.CreateConfigurationFingerprint(
                document.Llm,
                reportedModel,
                ModelCapabilityPolicy.ForcedToolTransportContractVersion,
                ModelCapabilityPolicy.ForcedToolTransportProbeVersion),
            document.Llm.ModelId,
            reportedModel,
            ModelCapabilityPolicy.ForcedToolTransportProbeVersion,
            calls,
            stopwatch.ElapsedMilliseconds,
            DateTimeOffset.UtcNow,
            results,
            selectedMode);
        var saved = await SaveForcedToolTransportCertificateAsync(certificate, cancellationToken)
            .ConfigureAwait(false);
        return BuildForcedToolTransportStatus(saved, _runtime.GetSnapshot(), certificate);
    }

    internal static ModelCapabilityStatus BuildForcedToolTransportStatus(
        SettingsDocument document,
        LlmRuntimeHealthSnapshot runtime,
        ModelCapabilityCertificate? justTested = null)
    {
        var settings = document.ModelCapabilities ?? new ModelCapabilitySettings();
        var reported = justTested?.ReportedModelId ?? runtime.ModelLoadedOrReported;
        if (string.IsNullOrWhiteSpace(reported) &&
            !string.Equals(document.Llm.ModelId, "auto", StringComparison.OrdinalIgnoreCase))
        {
            reported = document.Llm.ModelId;
        }
        var fingerprint = ModelCapabilityPolicy.CreateConfigurationFingerprint(
            document.Llm,
            reported,
            ModelCapabilityPolicy.ForcedToolTransportContractVersion,
            ModelCapabilityPolicy.ForcedToolTransportProbeVersion);
        var certificates = ModelCapabilityPolicy.GetCertificates(
            settings,
            ModelCapabilityPolicy.ForcedToolTransportCapability);
        var justTestedCurrent = justTested is not null &&
            string.Equals(justTested.ConfigurationFingerprint, fingerprint, StringComparison.Ordinal) &&
            string.Equals(
                justTested.ProbeVersion,
                ModelCapabilityPolicy.ForcedToolTransportProbeVersion,
                StringComparison.Ordinal);
        var currentCertificate = justTestedCurrent ? justTested : certificates.FirstOrDefault(certificate =>
            string.Equals(certificate.ConfigurationFingerprint, fingerprint, StringComparison.Ordinal) &&
            string.Equals(
                certificate.ProbeVersion,
                ModelCapabilityPolicy.ForcedToolTransportProbeVersion,
                StringComparison.Ordinal));
        var latestCertificate = certificates.OrderByDescending(certificate => certificate.TestedAt).FirstOrDefault();
        var certificate = currentCertificate ?? latestCertificate;
        var current = currentCertificate is not null;
        var certified = current && string.Equals(certificate?.Status, "certified", StringComparison.OrdinalIgnoreCase);
        var selectedMode = certified && string.Equals(certificate?.SelectedMode, "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : "required";
        var status = certificate is null ? "untested" : current ? certificate.Status : "stale";
        var message = certificate is null
            ? "Required remains active until this exact configuration is tested."
            : !current
                ? "The saved transport certificate does not match the current model configuration; required remains active."
                : certified
                    ? $"Certified forced-tool transport mode: {selectedMode}."
                    : "No alternate transport was certified; required remains active.";
        return new ModelCapabilityStatus(
            ModelCapabilityPolicy.ForcedToolTransportCapability,
            selectedMode,
            status,
            certified,
            current,
            fingerprint,
            message,
            certificate);
    }

    private async Task<SettingsDocument> SaveForcedToolTransportCertificateAsync(
        ModelCapabilityCertificate certificate,
        CancellationToken cancellationToken)
    {
        var current = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var settings = current.ModelCapabilities ?? new ModelCapabilitySettings();
        var otherCertificates = (settings.Certificates ?? []).Where(existing =>
            !string.Equals(
                ModelCapabilityPolicy.NormalizeCapabilityName(existing.Capability),
                ModelCapabilityPolicy.ForcedToolTransportCapability,
                StringComparison.Ordinal));
        var transportCertificates = ModelCapabilityPolicy.GetCertificates(
                settings,
                ModelCapabilityPolicy.ForcedToolTransportCapability)
            .Where(existing => !string.Equals(
                existing.ConfigurationFingerprint,
                certificate.ConfigurationFingerprint,
                StringComparison.Ordinal))
            .Append(certificate)
            .OrderByDescending(existing => existing.TestedAt)
            .Take(20);
        return await _settings.ReplaceAsync(current with
        {
            ModelCapabilities = settings with
            {
                Certificates = otherCertificates.Concat(transportCertificates).ToArray(),
            },
        }, cancellationToken).ConfigureAwait(false);
    }

    private static ToolDefinition BuildSyntheticTool(
        string name,
        string description,
        IReadOnlyDictionary<string, object> properties,
        IReadOnlyList<string> required) => new()
    {
        Function = new FunctionDefinition
        {
            Name = name,
            Description = description,
            Parameters = new
            {
                type = "object",
                properties,
                required,
                additionalProperties = false,
            },
        },
    };

    private static Task<LlmResponse> InvokeForcedToolTransportProbeAsync(
        ILlmClient client,
        ForcedToolTransportProbe probe,
        CancellationToken cancellationToken) =>
        client.ChatAsync(
            [
                LlmChatMessage.System(
                    "This is a synthetic, side-effect-free transport check. No tool call will be executed. " +
                    "Call the single advertised function exactly as requested and do not imitate a call in text."),
                LlmChatMessage.User(probe.UserPrompt),
            ],
            [probe.Tool],
            RetestMaxOutputTokens,
            probe.Tool.Function.Name,
            cancellationToken);

    private static ModelCapabilityProbeResult GradeForcedToolTransportProbe(
        string mode,
        ForcedToolTransportProbe probe,
        LlmResponse response)
    {
        var id = $"{mode}_{probe.Id}";
        if (response.ToolCalls is not { Count: 1 })
            return new(id, false, "Expected one effective structured call.");
        var call = response.ToolCalls[0];
        if (!string.Equals(call.Function.Name, probe.Tool.Function.Name, StringComparison.Ordinal))
            return new(id, false, "Effective function name did not match the advertised tool.");
        try
        {
            using var arguments = JsonDocument.Parse(call.Function.Arguments);
            if (arguments.RootElement.ValueKind != JsonValueKind.Object ||
                arguments.RootElement.EnumerateObject().Count() != probe.ExpectedArguments.Count)
            {
                return new(id, false, "Effective arguments did not have the exact object shape.");
            }
            foreach (var expected in probe.ExpectedArguments)
            {
                if (!arguments.RootElement.TryGetProperty(expected.Key, out var actual) ||
                    !MatchesExpectedValue(actual, expected.Value))
                {
                    return new(id, false, $"Effective argument {expected.Key} did not match exactly.");
                }
            }
            return new(id, true, "Produced one exact effective structured call.");
        }
        catch (JsonException)
        {
            return new(id, false, "Effective arguments were not valid JSON.");
        }
    }

    private static bool MatchesExpectedValue(JsonElement actual, object expected) => expected switch
    {
        string value => actual.ValueKind == JsonValueKind.String && actual.GetString() == value,
        int value => actual.ValueKind == JsonValueKind.Number && actual.TryGetInt32(out var parsed) && parsed == value,
        bool value => (actual.ValueKind is JsonValueKind.True or JsonValueKind.False) && actual.GetBoolean() == value,
        _ => false,
    };
}
