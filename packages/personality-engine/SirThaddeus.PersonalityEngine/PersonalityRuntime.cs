using System.Security.Cryptography;
using System.Text;
using SirThaddeus.PersonalityEngine.Context;
using SirThaddeus.PersonalityEngine.Formatting;
using SirThaddeus.PersonalityEngine.Profiles;
using SirThaddeus.PersonalityEngine.Prompting;

namespace SirThaddeus.PersonalityEngine;

public interface IPersonalityRuntime
{
    PersonalityRuntimeSnapshot Snapshot { get; }
    PersonalityRuntimeSnapshot Reload(string activeProfileId, string profilesDirectory);
    string BuildSystemPrompt(string taskInstruction, IEnumerable<PromptBlock>? extraBlocks = null);
    PersonalityTurnContext BuildTurnContext(string? latestUserMessage);
    string BuildAnchor(string turnTag, string? latestUserMessage = null);
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

    public PersonalityTurnContext BuildTurnContext(string? latestUserMessage)
    {
        var snapshot = Snapshot;
        return PersonalityTurnContextBuilder.Build(snapshot.Profile, latestUserMessage);
    }

    public string BuildAnchor(string turnTag, string? latestUserMessage = null)
    {
        var snapshot = Snapshot;
        var profile = snapshot.Profile;
        var tag = string.IsNullOrWhiteSpace(turnTag) ? "turn" : turnTag.Trim();
        var turnContext = BuildTurnContext(latestUserMessage);
        var tags = turnContext.Tags.Count == 0
            ? "none"
            : string.Join(", ", turnContext.Tags);

        var selfName = ResolveIdentityName(profile);
        var anchorBody =
            $"You are {selfName}. {profile.Description}. " +
            $"Context={tags} ({turnContext.Confidence:0.00}). " +
            $"Tone targets: directness={turnContext.EffectiveTone.Directness:0.00}, warmth={turnContext.EffectiveTone.Warmth:0.00}, humor={turnContext.EffectiveTone.Humor:0.00}, verbosity={turnContext.EffectiveTone.Verbosity:0.00}. " +
            $"Epistemic: no_invention={profile.EpistemicRules.NeverInventCapabilities}, admit_uncertainty={profile.EpistemicRules.AdmitUncertaintyExplicitly}. " +
            "Never bypass permissions, hide system state, or alter policy logic.";

        return
            $"[PERSONALITY_ANCHOR system:personality_anchor:v1:{tag}]\n" +
            $"{anchorBody}\n" +
            "[/PERSONALITY_ANCHOR]";
    }

    private static string BuildPersonalityBlock(PersonalityProfile profile, string hash)
    {
        var signature = profile.SpeechPatterns.IncludeSignatureNote ? "enabled" : "disabled";
        var reduction = ResolveReductionMode(profile.ReductionRules);
        var selfName = ResolveIdentityName(profile);
        var coreIdentity = ResolveCoreIdentity(profile);

        var sb = new StringBuilder();
        sb.AppendLine($"Profile id: {profile.Id}");
        sb.AppendLine($"Profile hash: {hash}");
        sb.AppendLine($"Your name: {selfName}");

        if (!string.IsNullOrWhiteSpace(coreIdentity))
            sb.AppendLine($"Core identity: {coreIdentity}");

        if (!string.IsNullOrWhiteSpace(profile.Identity.SelfDescription))
            sb.AppendLine($"Who you are (in your own words): {profile.Identity.SelfDescription}");

        sb.AppendLine($"Priority order: {FormatPriorityOrder(profile.Instructions.ResponsePriorityOrder)}");
        sb.AppendLine($"Tone: formality={profile.Tone.Formality:0.00}, warmth={profile.Tone.Warmth:0.00}, humor={profile.Tone.Humor:0.00}, verbosity={profile.Tone.Verbosity:0.00}, directness={profile.Tone.Directness:0.00}");
        // never_override_permissions is a runtime invariant, not a personality setting—enforced in Trust/Security blocks.
        sb.AppendLine($"Behavior: pushback_on_illogic={profile.BehaviorRules.PushbackOnIllogic}, avoid_flattery={profile.BehaviorRules.AvoidFlattery}");
        sb.AppendLine($"Epistemic: never_invent_capabilities={profile.EpistemicRules.NeverInventCapabilities}, admit_uncertainty_explicitly={profile.EpistemicRules.AdmitUncertaintyExplicitly}, ask_minimum_questions={profile.EpistemicRules.AskMinimumQuestions}");
        sb.AppendLine($"Speech: include_signature_note={signature}, avoid_modern_slang={profile.SpeechPatterns.AvoidModernSlang}");
        sb.AppendLine($"Constraints: max_metaphor_density={profile.CapabilityConstraints.MaxMetaphorDensity:0.00}, reduction={reduction}");
        AppendBulletList(sb, "Conflict rules", profile.Instructions.ConflictResolution, maxItems: 3);
        AppendBulletList(sb, "Failure behavior", profile.Instructions.FailureBehavior, maxItems: 2);
        AppendBulletList(sb, "Style rules", profile.Instructions.StyleRules, maxItems: 3);

        return sb.ToString();
    }

    private static string ResolveIdentityName(PersonalityProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Identity.SelfName)
            ? profile.DisplayName
            : profile.Identity.SelfName;

    private static string ResolveCoreIdentity(PersonalityProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Instructions.CoreIdentity))
            return profile.Instructions.CoreIdentity.Trim();

        return profile.Description;
    }

    private static string FormatPriorityOrder(IReadOnlyList<string> priorities)
    {
        var cleaned = priorities
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .ToList();

        return cleaned.Count == 0
            ? "safety > truth > clarity > agency > efficiency > tone"
            : string.Join(" > ", cleaned);
    }

    private static string ResolveReductionMode(PersonalityReductionRules rules)
    {
        var mode = (rules.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is "adaptive" or "always" or "never")
            return mode;

        return rules.Enabled ? "always" : "never";
    }

    private static void AppendBulletList(
        StringBuilder sb,
        string heading,
        IReadOnlyList<string> items,
        int maxItems)
    {
        var cleaned = items
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Take(maxItems)
            .ToList();
        if (cleaned.Count == 0)
            return;

        sb.AppendLine($"{heading}:");
        foreach (var item in cleaned)
            sb.AppendLine($"- {item}");
    }

    private static string ComputeTextHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
