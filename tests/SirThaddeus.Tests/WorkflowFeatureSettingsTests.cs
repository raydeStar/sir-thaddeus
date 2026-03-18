using SirThaddeus.Agent;
using SirThaddeus.Config;

namespace SirThaddeus.Tests;

public sealed class WorkflowFeatureSettingsTests
{
    [Fact]
    public void AppSettings_Defaults_WorkflowFeatures_HasEmptyOverrideReason()
    {
        var settings = new AppSettings();

        Assert.Equal("", settings.WorkflowFeatures.RetryGateTestOverrideReason);
    }

    [Fact]
    public void RuntimeControlState_FromSettings_MapsToolBudgets()
    {
        var settings = new AppSettings();

        var runtime = RuntimeControlState.FromSettings(settings);

        Assert.False(runtime.PanicModeEnabled);
        Assert.False(runtime.SafeModeEnabled);
        Assert.NotNull(runtime.ToolBudgets);
    }
}