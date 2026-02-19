using System.Text.RegularExpressions;
using SirThaddeus.PersonalityEngine;
using SirThaddeus.PersonalityEngine.Formatting;
using SirThaddeus.PersonalityEngine.Profiles;

namespace SirThaddeus.Tests;

public sealed class PersonalityV15ContractTests
{
    [Fact]
    public void TruthOverTone_ConflictRuleIsPresent()
    {
        var runtime = CreateRuntime();
        var prompt = runtime.BuildSystemPrompt("Task block.");

        Assert.Contains("Conflict rules:", prompt, StringComparison.Ordinal);
        Assert.Contains("prefer truth", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoInventedCapabilities_EpistemicGuardIsPresent()
    {
        var runtime = CreateRuntime();
        var prompt = runtime.BuildSystemPrompt("Task block.");

        Assert.Contains("never_invent_capabilities=True", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PermissionRespect_FailureBehaviorIncludesMinimalPlan()
    {
        var runtime = CreateRuntime();
        var prompt = runtime.BuildSystemPrompt("Task block.");

        Assert.Contains("Failure behavior:", prompt, StringComparison.Ordinal);
        Assert.Contains("minimal-permission plan", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmotionalUser_ContextModifierApplied()
    {
        var runtime = CreateRuntime();
        var baseTone = runtime.Snapshot.Profile.Tone;

        var context = runtime.BuildTurnContext(
            "I am feeling overwhelmed and anxious. I am honestly scared.");

        Assert.Contains("emotional_user", context.Tags, StringComparer.Ordinal);
        Assert.True(context.EffectiveTone.Warmth > baseTone.Warmth);
        Assert.True(context.EffectiveTone.Humor < baseTone.Humor);
        Assert.True(context.EffectiveTone.Directness < baseTone.Directness);
    }

    [Fact]
    public void TechnicalMode_ContextModifierApplied()
    {
        var runtime = CreateRuntime();
        var baseTone = runtime.Snapshot.Profile.Tone;

        var context = runtime.BuildTurnContext(
            "Can you help debug this stack trace and API contract issue?");

        Assert.Contains("technical_mode", context.Tags, StringComparer.Ordinal);
        Assert.True(context.EffectiveTone.Directness > baseTone.Directness);
        Assert.True(context.EffectiveTone.Verbosity < baseTone.Verbosity);
        Assert.True(context.EffectiveTone.Formality >= baseTone.Formality);
    }

    [Fact]
    public void ReductionAdaptive_SimpleQueriesReduce_ComplexQueriesPreserve()
    {
        var runtime = CreateRuntime();
        var profile = runtime.Snapshot.Profile;

        const string shortDuplicateResponse =
            "Walk. It is faster at that distance.\n\n" +
            "Walk. It is faster at that distance.\n\n" +
            "Need another quick one?";

        var shortOptions = PersonalityFormattingPolicy.BuildReductionOptions(
            profile,
            latestUserMessage: "walk or drive 50 meters?");
        var shortOutput = ReductionFormatter.Apply(shortDuplicateResponse, shortOptions);

        var shortDuplicateCount = Regex.Matches(
            shortOutput,
            Regex.Escape("Walk. It is faster at that distance.")).Count;
        Assert.Equal(1, shortDuplicateCount);

        var longParagraph = new string('x', 420);
        var longDuplicateResponse =
            $"{longParagraph}\n\n{longParagraph}\n\n{new string('y', 200)}";

        var longOptions = PersonalityFormattingPolicy.BuildReductionOptions(
            profile,
            latestUserMessage: "Please compare three commuting strategies with tradeoffs, caveats, and contingencies.");
        var longOutput = ReductionFormatter.Apply(longDuplicateResponse, longOptions);

        var longDuplicateCount = Regex.Matches(
            longOutput,
            Regex.Escape(longParagraph)).Count;
        Assert.Equal(2, longDuplicateCount);
    }

    private static PersonalityRuntime CreateRuntime()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"personality-v15-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new PersonalityRuntime(BuiltInProfileCatalog.SirThaddeusId, dir);
    }
}
