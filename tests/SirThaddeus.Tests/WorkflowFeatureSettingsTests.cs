using SirThaddeus.Agent;
using SirThaddeus.Config;

namespace SirThaddeus.Tests;

public sealed class WorkflowFeatureSettingsTests
{
    [Fact]
    public void AppSettings_Defaults_WorkflowFeaturesAreDisabled()
    {
        var settings = new AppSettings();

        Assert.False(settings.WorkflowFeatures.ChecklistProgressUiEnabled);
        Assert.False(settings.WorkflowFeatures.ConfidenceScoringEnabled);
        Assert.False(settings.WorkflowFeatures.ConstrainedRetryEnabled);
        Assert.False(settings.WorkflowFeatures.TaskRunAuditSnapshotsEnabled);
    }

    [Fact]
    public void RuntimeControlState_FromSettings_CarriesWorkflowFeatureFlags()
    {
        var settings = new AppSettings
        {
            WorkflowFeatures = new WorkflowFeatureSettings
            {
                ConfidenceScoringEnabled = true
            }
        };

        var runtime = RuntimeControlState.FromSettings(settings);

        Assert.True(runtime.WorkflowFeatures.ConfidenceScoringEnabled);
        Assert.False(runtime.WorkflowFeatures.ChecklistProgressUiEnabled);
        Assert.False(runtime.WorkflowFeatures.ConstrainedRetryEnabled);
        Assert.False(runtime.WorkflowFeatures.TaskRunAuditSnapshotsEnabled);
    }

    [Fact]
    public void RuntimeControlState_IsChecklistWorkflowEnabled_TrueWhenAnyFlagEnabled()
    {
        var settings = new AppSettings
        {
            WorkflowFeatures = new WorkflowFeatureSettings
            {
                TaskRunAuditSnapshotsEnabled = true
            }
        };

        var runtime = RuntimeControlState.FromSettings(settings);

        Assert.True(runtime.IsChecklistWorkflowEnabled);
    }
}