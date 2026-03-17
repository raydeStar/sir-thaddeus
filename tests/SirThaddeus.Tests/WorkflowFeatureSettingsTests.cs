using SirThaddeus.Agent;
using SirThaddeus.Config;

namespace SirThaddeus.Tests;

public sealed class WorkflowFeatureSettingsTests
{
    [Fact]
    public void AppSettings_Defaults_WorkflowFeaturesAreEnabled()
    {
        var settings = new AppSettings();

        Assert.True(settings.WorkflowFeatures.ChecklistProgressUiEnabled);
        Assert.True(settings.WorkflowFeatures.ConfidenceScoringEnabled);
        Assert.True(settings.WorkflowFeatures.ConstrainedRetryEnabled);
        Assert.True(settings.WorkflowFeatures.TaskRunAuditSnapshotsEnabled);
    }

    [Fact]
    public void RuntimeControlState_FromSettings_CarriesWorkflowFeatureFlags()
    {
        var settings = new AppSettings
        {
            WorkflowFeatures = new WorkflowFeatureSettings
            {
                ChecklistProgressUiEnabled = false,
                ConfidenceScoringEnabled = true,
                ConstrainedRetryEnabled = false,
                TaskRunAuditSnapshotsEnabled = false
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