using SirThaddeus.AuditLog;
using SirThaddeus.Core;
using SirThaddeus.Invocation;
using SirThaddeus.PermissionBroker;
using SirThaddeus.ToolRunner;
using SirThaddeus.ToolRunner.Tools;

namespace SirThaddeus.Tests;

/// <summary>
/// Tests for the invocation/orchestration layer.
/// </summary>
public class InvocationTests
{
    // ─────────────────────────────────────────────────────────────────
    // CommandPlanner Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_OpenUrl_ParsesCorrectly()
    {
        var planner = new CommandPlanner();
        
        var result = planner.Plan("open https://example.com");
        
        Assert.True(result.Success);
        Assert.NotNull(result.Plan);
        Assert.Single(result.Plan.Steps);
        Assert.Equal("browser_navigate", result.Plan.Steps[0].Name);
        Assert.Equal(Capability.BrowserControl, result.Plan.Steps[0].RequiredCapability);
        Assert.Equal("https://example.com/", result.Plan.Steps[0].Arguments?["url"]?.ToString());
    }

    [Fact]
    public void Plan_OpenUrl_AddsHttpsPrefix()
    {
        var planner = new CommandPlanner();
        
        var result = planner.Plan("open example.com");
        
        Assert.True(result.Success);
        Assert.Equal("https://example.com/", result.Plan!.Steps[0].Arguments?["url"]?.ToString());
    }

    [Fact]
    public void Plan_OpenUrl_InvalidUrl_ReturnsFail()
    {
        var planner = new CommandPlanner();
        
        var result = planner.Plan("open not a valid url ::: broken");
        
        Assert.False(result.Success);
        Assert.Contains("Invalid URL", result.Error);
    }

    [Fact]
    public void Plan_SpecNew_ReturnsTemplate()
    {
        var planner = new CommandPlanner();
        
        var result = planner.Plan("spec new");
        
        Assert.True(result.Success);
        Assert.NotNull(result.DirectOutput);
        Assert.Contains("version", result.DirectOutput);
        Assert.Contains("target", result.DirectOutput);
        Assert.Contains("check", result.DirectOutput);
    }

    [Fact]
    public void Plan_CaptureScreen_CreatesToolCall()
    {
        var planner = new CommandPlanner();
        
        var result = planner.Plan("capture screen");
        
        Assert.True(result.Success);
        Assert.NotNull(result.Plan);
        Assert.Single(result.Plan.Steps);
        Assert.Equal("screen_capture", result.Plan.Steps[0].Name);
        Assert.Equal(Capability.ScreenRead, result.Plan.Steps[0].RequiredCapability);
    }

    [Fact]
    public void Plan_UnknownCommand_ReturnsFail()
    {
        var planner = new CommandPlanner();
        
        var result = planner.Plan("unknown command here");
        
        Assert.False(result.Success);
        Assert.Contains("Unknown command", result.Error);
    }

    [Fact]
    public void Plan_EmptyCommand_ReturnsFail()
    {
        var planner = new CommandPlanner();
        
        var result = planner.Plan("   ");
        
        Assert.False(result.Success);
        Assert.Contains("No command", result.Error);
    }

    // ─────────────────────────────────────────────────────────────────
    // ToolPlanExecutor Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Executor_GrantsPermission_ExecutesTool()
    {
        // Arrange
        var auditLogger = new TestAuditLogger();
        var broker = new InMemoryPermissionBroker(auditLogger);
        var runner = new EnforcingToolRunner(broker, auditLogger);
        runner.RegisterTool(new ScreenCaptureTool());
        
        var host = new TestToolExecutionHost();
        var prompter = new AutoApprovePrompter();
        var executor = new ToolPlanExecutor(broker, runner, prompter, host);

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test capture",
            RequiredCapability = Capability.ScreenRead,
            Arguments = new Dictionary<string, object> { ["target"] = "active_window" }
        };
        var plan = ToolPlan.SingleStep(call, "Capture screen");

        // Act
        var result = await executor.ExecuteAsync(plan);

        // Assert
        Assert.True(result.Success);
        Assert.Single(result.StepResults);
        Assert.True(result.StepResults[0].Success);
    }

    [Fact]
    public async Task Executor_DeniesPermission_ReturnsFailure()
    {
        // Arrange
        var auditLogger = new TestAuditLogger();
        var broker = new InMemoryPermissionBroker(auditLogger);
        var runner = new EnforcingToolRunner(broker, auditLogger);
        runner.RegisterTool(new ScreenCaptureTool());
        
        var host = new TestToolExecutionHost();
        var prompter = new AutoDenyPrompter("User said no");
        var executor = new ToolPlanExecutor(broker, runner, prompter, host);

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test capture",
            RequiredCapability = Capability.ScreenRead,
            Arguments = new Dictionary<string, object>()
        };
        var plan = ToolPlan.SingleStep(call, "Capture screen");

        // Act
        var result = await executor.ExecuteAsync(plan);

        // Assert
        Assert.False(result.Success);
        Assert.Single(result.StepResults);
        Assert.False(result.StepResults[0].Success);
        Assert.Contains("denied", result.StepResults[0].Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Executor_DeniesPermission_LogsDenial()
    {
        // Arrange
        var auditLogger = new TestAuditLogger();
        var broker = new InMemoryPermissionBroker(auditLogger);
        var runner = new EnforcingToolRunner(broker, auditLogger);
        runner.RegisterTool(new ScreenCaptureTool());
        
        var host = new TestToolExecutionHost();
        var prompter = new AutoDenyPrompter("Privacy concern");
        var executor = new ToolPlanExecutor(broker, runner, prompter, host);

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test capture",
            RequiredCapability = Capability.ScreenRead,
            Arguments = new Dictionary<string, object>()
        };
        var plan = ToolPlan.SingleStep(call, "Capture screen");

        // Act
        await executor.ExecuteAsync(plan);

        // Assert
        var denialEvent = auditLogger.GetByAction("PERMISSION_DENIED").FirstOrDefault();
        Assert.NotNull(denialEvent);
        Assert.Contains("Privacy concern", denialEvent.Details?["reason"]?.ToString());
    }

    [Fact]
    public async Task Executor_Cancellation_StopsExecution()
    {
        // Arrange
        var auditLogger = new TestAuditLogger();
        var broker = new InMemoryPermissionBroker(auditLogger);
        var runner = new EnforcingToolRunner(broker, auditLogger);
        runner.RegisterTool(new ScreenCaptureTool());
        
        var host = new TestToolExecutionHost();
        var prompter = new AutoApprovePrompter();
        var executor = new ToolPlanExecutor(broker, runner, prompter, host);

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test capture",
            RequiredCapability = Capability.ScreenRead,
            Arguments = new Dictionary<string, object>()
        };
        var plan = ToolPlan.SingleStep(call, "Capture screen");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await executor.ExecuteAsync(plan, cts.Token);

        // Assert
        Assert.False(result.Success);
        Assert.True(result.WasCancelled);
    }

    // ─────────────────────────────────────────────────────────────────
    // Scope Enforcement Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BrowserNavigate_DomainScope_AllowsMatchingDomain()
    {
        // Arrange
        var auditLogger = new TestAuditLogger();
        var broker = new InMemoryPermissionBroker(auditLogger);
        var runner = new EnforcingToolRunner(broker, auditLogger);
        runner.RegisterTool(new BrowserNavigateTool());

        var request = new PermissionRequest
        {
            Capability = Capability.BrowserControl,
            Purpose = "Test navigation",
            Duration = TimeSpan.FromMinutes(5),
            Requester = "test",
            Scope = PermissionScope.ForDomain("example.com")
        };
        var token = broker.IssueToken(request);

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "browser_navigate",
            Purpose = "Navigate to example",
            RequiredCapability = Capability.BrowserControl,
            Arguments = new Dictionary<string, object> { ["url"] = "https://example.com/page" }
        };

        // Act
        var result = await runner.ExecuteAsync(call, token.Id);

        // Assert
        Assert.True(result.Success);
    }

    [Fact]
    public async Task BrowserNavigate_DomainScope_RejectsNonMatchingDomain()
    {
        // Arrange
        var auditLogger = new TestAuditLogger();
        var broker = new InMemoryPermissionBroker(auditLogger);
        var runner = new EnforcingToolRunner(broker, auditLogger);
        runner.RegisterTool(new BrowserNavigateTool());

        var request = new PermissionRequest
        {
            Capability = Capability.BrowserControl,
            Purpose = "Test navigation",
            Duration = TimeSpan.FromMinutes(5),
            Requester = "test",
            Scope = PermissionScope.ForDomain("example.com")
        };
        var token = broker.IssueToken(request);

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "browser_navigate",
            Purpose = "Navigate to evil site",
            RequiredCapability = Capability.BrowserControl,
            Arguments = new Dictionary<string, object> { ["url"] = "https://evil.com/steal" }
        };

        // Act
        var result = await runner.ExecuteAsync(call, token.Id);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not allowed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FileRead_PathScope_AllowsMatchingPath()
    {
        // Arrange
        var auditLogger = new TestAuditLogger();
        var broker = new InMemoryPermissionBroker(auditLogger);
        var runner = new EnforcingToolRunner(broker, auditLogger);
        runner.RegisterTool(new FileReadTool());

        var request = new PermissionRequest
        {
            Capability = Capability.FileAccess,
            Purpose = "Test file read",
            Duration = TimeSpan.FromMinutes(5),
            Requester = "test",
            Scope = PermissionScope.ForPath(@"C:\allowed\folder")
        };
        var token = broker.IssueToken(request);

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "file_read",
            Purpose = "Read config",
            RequiredCapability = Capability.FileAccess,
            Arguments = new Dictionary<string, object> { ["path"] = @"C:\allowed\folder\config.json" }
        };

        // Act
        var result = await runner.ExecuteAsync(call, token.Id);

        // Assert
        Assert.True(result.Success);
    }

    [Fact]
    public async Task FileRead_PathScope_RejectsNonMatchingPath()
    {
        // Arrange
        var auditLogger = new TestAuditLogger();
        var broker = new InMemoryPermissionBroker(auditLogger);
        var runner = new EnforcingToolRunner(broker, auditLogger);
        runner.RegisterTool(new FileReadTool());

        var request = new PermissionRequest
        {
            Capability = Capability.FileAccess,
            Purpose = "Test file read",
            Duration = TimeSpan.FromMinutes(5),
            Requester = "test",
            Scope = PermissionScope.ForPath(@"C:\allowed\folder")
        };
        var token = broker.IssueToken(request);

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "file_read",
            Purpose = "Read secrets",
            RequiredCapability = Capability.FileAccess,
            Arguments = new Dictionary<string, object> { ["path"] = @"C:\secrets\passwords.txt" }
        };

        // Act
        var result = await runner.ExecuteAsync(call, token.Id);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not allowed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────
    // Helper Classes
    // ─────────────────────────────────────────────────────────────────

    private sealed class TestToolExecutionHost : IToolExecutionHost
    {
        private readonly CancellationTokenSource _cts = new();
        
        public AssistantState CurrentState { get; private set; } = AssistantState.Idle;
        public CancellationToken RuntimeToken => _cts.Token;
        
        public bool SetState(AssistantState state, string? reason = null)
        {
            CurrentState = state;
            return true;
        }

        public void RefreshAuditFeed() { }
    }
}
