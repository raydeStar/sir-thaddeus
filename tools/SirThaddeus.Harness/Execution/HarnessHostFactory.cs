using SirThaddeus.Config;
using SirThaddeus.Harness.Cli;

namespace SirThaddeus.Harness.Execution;

/// <summary>
/// Picks an <see cref="IHarnessHostAdapter"/> based on the user's
/// <c>--target</c> selection. v1 is the only fully-implemented path
/// today; the v2 adapter throws on construction with a clear message
/// pointing to the work needed to wire it up.
/// </summary>
internal static class HarnessHostFactory
{
    public static IHarnessHostAdapter Create(HarnessCommandOptions options, AppSettings settings)
    {
        return options.HostTarget switch
        {
            HarnessHostTarget.HeadlessV1 => new HeadlessRuntimeHarnessClient(settings),
            HarnessHostTarget.HybridV2 => new HybridRuntimeHostAdapter(settings),
            _ => throw new InvalidOperationException(
                $"Unhandled host target '{options.HostTarget}'.")
        };
    }
}
