using SirThaddeus.Config;

namespace SirThaddeus.Agent;

/// <summary>
/// Immutable runtime control snapshot consumed by enforcement layers.
/// </summary>
public sealed record RuntimeControlState
{
    public bool PanicModeEnabled { get; init; }
    public bool SafeModeEnabled { get; init; }
    public ToolBudgetSettings ToolBudgets { get; init; } = new();

    public static RuntimeControlState FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new RuntimeControlState
        {
            PanicModeEnabled = settings.RuntimeSafety.PanicMode,
            SafeModeEnabled = settings.RuntimeSafety.SafeMode,
            ToolBudgets = settings.ToolBudgets.Normalize()
        };
    }
}
