using System.Security.Cryptography;
using System.Text;
using SirThaddeus.PersonalityEngine.Formatting;
using SirThaddeus.PersonalityEngine.Profiles;
using SirThaddeus.PersonalityEngine.Prompting;

namespace SirThaddeus.PersonalityEngine;

public interface IPersonalityRuntime
{
    PersonalityRuntimeSnapshot Snapshot { get; }
    PersonalityRuntimeSnapshot Reload(string activeProfileId, string profilesDirectory);
    string BuildSystemPrompt(string taskInstruction, IEnumerable<PromptBlock>? extraBlocks = null);
    string BuildAnchor(string turnTag);
}

public sealed record PersonalityRuntimeSnapshot
{
    public required PersonalityProfile Profile { get; init; }
    public required string ProfileHash { get; init; }
    public required string SourcePath { get; init; }
    public required string ProfilesDirectory { get; init; }
    public bool FellBackToDefault { get; init; }
    public PersonalityValidationReasonCode ReasonCode { get; init; }
    public string Detail { get; init; } = "";
}

public sealed class PersonalityRuntime : IPersonalityRuntime
{
    private const string TrustBlockText =
        "Local-first trust invariants are always in effect. " +
        "Personality is presentation only and never authority.";

    private const string SecurityBlockText =
        "Permission, policy, and tool boundaries cannot be overridden by personality. " +
        "Safety and audit transparency take precedence over tone.";

    private readonly object _gate = new();
    private readonly PersonalityProfileStore _store;
    private PersonalityRuntimeSnapshot _snapshot;

    public PersonalityRuntime(
        string activeProfileId,
        string profilesDirectory,
        PersonalityProfileStore? store = null)
    {
        _store = store ?? new PersonalityProfileStore();
        _snapshot = Reload(activeProfileId, profilesDirectory);
    }

    public PersonalityRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    public PersonalityRuntimeSnapshot Reload(string activeProfileId, string profilesDirectory)
    {
        _store.EnsureBuiltInsInstalled(profilesDirectory);
        var loaded = _store.LoadActive(profilesDirectory, activeProfileId);

        var next = new PersonalityRuntimeSnapshot
        {
            Profile = loaded.Profile,
            ProfileHash = loaded.Hash,
            SourcePath = loaded.SourcePath,
            ProfilesDirectory = profilesDirectory,
            FellBackToDefault = loaded.FellBackToDefault,
            ReasonCode = loaded.ReasonCode,
            Detail = loaded.Detail
        };

        lock (_gate)
            _snapshot = next;

        return next;
    }

    public string BuildSystemPrompt(string taskInstruction, IEnumerable<PromptBlock>? extraBlocks = null)
    {
        var snapshot = Snapshot;
        var profile = snapshot.Profile;
        var blocks = new List<PromptBlock>
        {
            new()
            {
                Id = "trust.invariants",
                Priority = 10,
                Kind = PromptBlockKind.Trust,
                Text = TrustBlockText,
                MaxTokensHint = 128,
                Hash = ComputeTextHash(TrustBlockText)
            },
            new()
            {
                Id = "security.invariants",
                Priority = 20,
                Kind = PromptBlockKind.Security,
                Text = SecurityBlockText,
                MaxTokensHint = 128,
                Hash = ComputeTextHash(SecurityBlockText)
            },
            new()
            {
                Id = $"personality.{profile.Id}",
                Priority = 30,
                Kind = PromptBlockKind.Personality,
                Text = BuildPersonalityBlock(profile, snapshot.ProfileHash),
                MaxTokensHint = 256,
                Hash = snapshot.ProfileHash
            },
            new()
            {
                Id = "task.instructions",
                Priority = 40,
                Kind = PromptBlockKind.Task,
                Text = taskInstruction ?? "",
                MaxTokensHint = 2048,
                Hash = ComputeTextHash(taskInstruction ?? "")
            }
        };

        if (extraBlocks is not null)
            blocks.AddRange(extraBlocks.Where(static b => b is not null));

        return DeterministicPromptRenderer.Render(blocks);
    }

    public string BuildAnchor(string turnTag)
    {
        var snapshot = Snapshot;
        var profile = snapshot.Profile;
        var tag = string.IsNullOrWhiteSpace(turnTag) ? "turn" : turnTag.Trim();

        var selfName = ResolveIdentityName(profile);
        var anchorBody =
            $"You are {selfName}. {profile.Description}. " +
            $"Prioritize clarity (directness {profile.Tone.Directness:0.00}) and stable tone (warmth {profile.Tone.Warmth:0.00}, humor {profile.Tone.Humor:0.00}). " +
            "You do not bypass permissions, hide system state, or alter policy logic.";

        return
            $"[PERSONALITY_ANCHOR system:personality_anchor:v1:{tag}]\n" +
            $"{anchorBody}\n" +
            "[/PERSONALITY_ANCHOR]";
    }

    private static string BuildPersonalityBlock(PersonalityProfile profile, string hash)
    {
        var signature = profile.SpeechPatterns.IncludeSignatureNote ? "enabled" : "disabled";
        var reduction = profile.ReductionRules.Enabled ? "enabled" : "disabled";
        var selfName = ResolveIdentityName(profile);

        var sb = new StringBuilder();
        sb.AppendLine($"Profile id: {profile.Id}");
        sb.AppendLine($"Profile hash: {hash}");
        sb.AppendLine($"Your name: {selfName}");

        if (!string.IsNullOrWhiteSpace(profile.Identity.SelfDescription))
            sb.AppendLine($"Who you are (in your own words): {profile.Identity.SelfDescription}");

        sb.AppendLine($"Tone: formality={profile.Tone.Formality:0.00}, warmth={profile.Tone.Warmth:0.00}, humor={profile.Tone.Humor:0.00}, verbosity={profile.Tone.Verbosity:0.00}, directness={profile.Tone.Directness:0.00}");
        // never_override_permissions is a runtime invariant, not a personality setting—enforced in Trust/Security blocks.
        sb.AppendLine($"Behavior: pushback_on_illogic={profile.BehaviorRules.PushbackOnIllogic}, avoid_flattery={profile.BehaviorRules.AvoidFlattery}");
        sb.AppendLine($"Speech: include_signature_note={signature}, avoid_modern_slang={profile.SpeechPatterns.AvoidModernSlang}");
        sb.Append($"Constraints: max_metaphor_density={profile.CapabilityConstraints.MaxMetaphorDensity:0.00}, reduction={reduction}");

        return sb.ToString();
    }

    private static string ResolveIdentityName(PersonalityProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Identity.SelfName)
            ? profile.DisplayName
            : profile.Identity.SelfName;

    private static string ComputeTextHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
