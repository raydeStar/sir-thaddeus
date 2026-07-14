using SirThaddeus.Agent;
using SirThaddeus.Config;
using SirThaddeus.Harness.Cli;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Execution;

/// <summary>
/// Picks an <see cref="IHarnessHostAdapter"/> based on the user's
/// <c>--target</c> selection. v1 is the only fully-implemented path
/// today; the v2 adapter throws on construction with a clear message
/// pointing to the work needed to wire it up.
/// </summary>
internal static class HarnessHostFactory
{
    public static IHarnessHostAdapter Create(
        HarnessCommandOptions options,
        AppSettings settings,
        IReadOnlyList<HarnessSuite> suites)
    {
        var requirements = HarnessHostRequirements.FromSuites(suites);
        return options.HostTarget switch
        {
            HarnessHostTarget.HeadlessV1 => new HeadlessRuntimeHarnessClient(settings, requirements.RequiresManagedSearch),
            HarnessHostTarget.HybridV2 => new HybridRuntimeHostAdapter(settings),
            _ => throw new InvalidOperationException(
                $"Unhandled host target '{options.HostTarget}'.")
        };
    }
}

internal sealed record HarnessHostRequirements(bool RequiresManagedSearch)
{
    public static HarnessHostRequirements FromSuites(IReadOnlyList<HarnessSuite> suites)
    {
        var requiresSearch = suites
            .SelectMany(suite => suite.Tests)
            .Any(test =>
                !test.Assertions.AllowedToolsOnly ||
                test.AllowedTools.Any(tool =>
                    string.Equals(tool, ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tool, ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase)));

        return new HarnessHostRequirements(requiresSearch);
    }
}
