using SirThaddeus.PersonalityEngine;
using SirThaddeus.PersonalityEngine.Profiles;
using SirThaddeus.PersonalityEngine.Prompting;
using SirThaddeus.LlmClient;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    private readonly IPersonalityRuntime _personalityRuntime;
    private string _activePersonalityId = BuiltInProfileCatalog.HelpfulDefaultId;
    private string _personalityProfilesDirectory = "";
    private int _turnSequence;

    /// <summary>
    /// Active deterministic personality id.
    /// </summary>
    public string ActivePersonalityId
    {
        get => _activePersonalityId;
        set
        {
            var normalized = NormalizePersonalityId(value);
            if (string.Equals(_activePersonalityId, normalized, StringComparison.Ordinal))
                return;

            _activePersonalityId = normalized;
            ReloadPersonalityRuntime();
        }
    }

    /// <summary>
    /// Filesystem directory containing personality profile JSON files.
    /// </summary>
    public string PersonalityProfilesDirectory
    {
        get => _personalityProfilesDirectory;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? ResolveDefaultPersonalityProfilesDirectory()
                : value.Trim();

            if (string.Equals(_personalityProfilesDirectory, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _personalityProfilesDirectory = normalized;
            ReloadPersonalityRuntime();
        }
    }

    /// <summary>
    /// Hash of the currently active canonical personality profile.
    /// </summary>
    public string ActivePersonalityHash => _personalityRuntime.Snapshot.ProfileHash;

    private string BuildEffectiveSystemPrompt()
    {
        var extraBlocks = new List<PromptBlock>();

        if (!string.IsNullOrWhiteSpace(UserLocationHint))
        {
            var locationBlock =
                $"The user's home location is: {UserLocationHint.Trim()}." +
                (string.IsNullOrWhiteSpace(UserTimezone)
                    ? ""
                    : $" Timezone: {UserTimezone.Trim()}.") +
                " Use this as the default area when they ask about local " +
                "businesses, weather, news, or places without specifying a " +
                "location. Do NOT announce that you know their location - " +
                "just use it naturally.";

            extraBlocks.Add(new PromptBlock
            {
                Id = "mode.user_location",
                Priority = 50,
                Kind = PromptBlockKind.Mode,
                Text = locationBlock,
                MaxTokensHint = 180
            });
        }

        var units = (PreferredUnits ?? "").Trim().ToLowerInvariant();
        if (units is "imperial" or "metric")
        {
            var unitDesc = units == "imperial"
                ? "imperial (°F, mph, miles, inches)"
                : "metric (°C, km/h, kilometers, millimeters)";
            var unitsBlock =
                $"Present all weather data, measurements, and distances in {unitDesc} " +
                "unless the user explicitly requests a different unit (e.g. \"what's that in celsius?\"). " +
                "When the user asks for conversion, show the converted value naturally.";

            extraBlocks.Add(new PromptBlock
            {
                Id = "mode.unit_preference",
                Priority = 55,
                Kind = PromptBlockKind.Mode,
                Text = unitsBlock,
                MaxTokensHint = 120
            });
        }

        return _personalityRuntime.BuildSystemPrompt(_systemPrompt, extraBlocks);
    }

    private static string NormalizeUnitPreference(string? value)
    {
        var lower = (value ?? "").Trim().ToLowerInvariant();
        return lower switch
        {
            "imperial" => "imperial",
            "metric" => "metric",
            _ => "auto"
        };
    }

    private static string NormalizePersonalityId(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized)
            ? BuiltInProfileCatalog.HelpfulDefaultId
            : normalized;
    }

    private static string ResolveDefaultPersonalityProfilesDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SirThaddeus", "profiles");
    }

    private void ReloadPersonalityRuntime()
    {
        var snapshot = _personalityRuntime.Reload(_activePersonalityId, _personalityProfilesDirectory);
        _searchOrchestrator.SystemPrompt = _personalityRuntime.BuildSystemPrompt(_systemPrompt);
        ReplaceBaseSystemPrompt();
        EmitPersonalityAuditSnapshot(snapshot, _activePersonalityId);
    }

    private void ReplaceBaseSystemPrompt()
    {
        var nextSystemPrompt = BuildEffectiveSystemPrompt();
        for (var i = 0; i < _history.Count; i++)
        {
            if (_history[i].Role != "system")
                continue;

            _history[i] = ChatMessage.System(nextSystemPrompt);
            return;
        }

        _history.Insert(0, ChatMessage.System(nextSystemPrompt));
    }

    private void EmitPersonalityAuditSnapshot(
        PersonalityRuntimeSnapshot snapshot,
        string requestedProfileId)
    {
        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = "PERSONALITY_PROFILE_ACTIVATED",
            Result = snapshot.FellBackToDefault ? "fallback" : "ok",
            Details = new Dictionary<string, object>
            {
                ["requested_profile_id"] = requestedProfileId,
                ["profile_id"] = snapshot.Profile.Id,
                ["profile_hash"] = snapshot.ProfileHash,
                ["path"] = Path.GetFileName(snapshot.SourcePath)
            }
        });

        if (snapshot.ReasonCode == PersonalityValidationReasonCode.None)
            return;

        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = "PERSONALITY_PROFILE_REJECTED",
            Result = "fallback",
            Details = new Dictionary<string, object>
            {
                ["reason_code"] = snapshot.ReasonCode.ToString(),
                ["requested_profile_id"] = requestedProfileId,
                ["profile_id"] = snapshot.Profile.Id,
                ["profile_hash"] = snapshot.ProfileHash,
                ["path"] = Path.GetFileName(snapshot.SourcePath),
                ["fallback_to"] = snapshot.Profile.Id
            }
        });
    }
}
