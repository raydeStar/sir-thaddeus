using SirThaddeus.Agent;

namespace SirThaddeus.Tests.Agent;

public sealed class ProductionPromptComposerTests
{
    [Fact]
    public void Base_prompt_composition_is_deterministic_and_keeps_preamble_order()
    {
        var prompt = ProductionPromptComposer.ComposeBaseSystemPrompt(
            "BASE",
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            "Denver, CO",
            "America/Denver",
            "imperial",
            offlineMode: true);

        var date = prompt.IndexOf("2026-07-14", StringComparison.Ordinal);
        var location = prompt.IndexOf("Denver, CO", StringComparison.Ordinal);
        var offline = prompt.IndexOf("Offline mode is ON", StringComparison.Ordinal);
        var basePrompt = prompt.IndexOf("BASE", StringComparison.Ordinal);
        Assert.True(date >= 0 && date < location);
        Assert.True(location < offline);
        Assert.True(offline < basePrompt);
        Assert.Contains("Timezone: America/Denver.", prompt, StringComparison.Ordinal);
        Assert.Contains("Preferred units: imperial.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Base_prompt_without_location_has_only_date_and_task_blocks()
    {
        var prompt = ProductionPromptComposer.ComposeBaseSystemPrompt(
            "BASE",
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));

        Assert.Contains("2026-07-14", prompt, StringComparison.Ordinal);
        Assert.EndsWith("\n\nBASE", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("home location", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Offline mode", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_agent_assembly_has_no_benchmark_or_scorer_reference()
    {
        var references = typeof(ProductionPromptComposer).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name =>
            name.Contains("bench", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("scorer", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mmlu", StringComparison.OrdinalIgnoreCase));
    }
}
