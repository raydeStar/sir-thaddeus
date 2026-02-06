using SirThaddeus.AuditLog;
using SirThaddeus.Core;

namespace SirThaddeus.Tests;

/// <summary>
/// Tests for the RuntimeController.
/// </summary>
public class RuntimeControllerTests : IDisposable
{
    private readonly string _testFilePath;
    private readonly JsonLineAuditLogger _auditLogger;

    public RuntimeControllerTests()
    {
        _testFilePath = Path.Combine(
            Path.GetTempPath(),
            $"meaningful_copilot_test_{Guid.NewGuid()}.jsonl");
        _auditLogger = new JsonLineAuditLogger(_testFilePath);
    }

    public void Dispose()
    {
        _auditLogger.Dispose();
        if (File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
    }

    [Fact]
    public void Constructor_InitializesWithIdleState()
    {
        // Act
        using var controller = new RuntimeController(_auditLogger);

        // Assert
        Assert.Equal(AssistantState.Idle, controller.CurrentState);
        Assert.False(controller.IsStopped);
    }

    [Fact]
    public void Constructor_LogsInitialStateChange()
    {
        // Act
        using var controller = new RuntimeController(_auditLogger);

        // Assert
        var events = _auditLogger.ReadTail(10);
        Assert.Contains(events, e => e.Action == "STATE_CHANGE");
    }

    [Fact]
    public void SetState_ChangesCurrentState()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);

        // Act
        var result = controller.SetState(AssistantState.Listening);

        // Assert
        Assert.True(result);
        Assert.Equal(AssistantState.Listening, controller.CurrentState);
    }

    [Fact]
    public void SetState_RaisesStateChangedEvent()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);
        StateChangedEventArgs? receivedArgs = null;
        controller.StateChanged += (_, args) => receivedArgs = args;

        // Act
        controller.SetState(AssistantState.Thinking);

        // Assert
        Assert.NotNull(receivedArgs);
        Assert.Equal(AssistantState.Idle, receivedArgs.PreviousState);
        Assert.Equal(AssistantState.Thinking, receivedArgs.NewState);
    }

    [Fact]
    public void SetState_LogsTransitionToAuditLog()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);

        // Act
        controller.SetState(AssistantState.ReadingScreen, "User requested screen read");

        // Assert
        var events = _auditLogger.ReadTail(10);
        var transition = events.Last();
        
        Assert.Equal("STATE_CHANGE", transition.Action);
        Assert.Equal("ReadingScreen", transition.Target);
        Assert.NotNull(transition.Details);
        Assert.Equal("Idle", transition.Details["from"].ToString());
        Assert.Equal("ReadingScreen", transition.Details["to"].ToString());
    }

    [Fact]
    public void SetState_ReturnsTrueForSameState_NoEvent()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);
        var initialEventCount = _auditLogger.ReadTail(100).Count;

        // Act - set to same state
        var result = controller.SetState(AssistantState.Idle);

        // Assert
        Assert.True(result);
        var currentEventCount = _auditLogger.ReadTail(100).Count;
        Assert.Equal(initialEventCount, currentEventCount); // No new events
    }

    [Fact]
    public void StopAll_SetsStateToOff()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);
        controller.SetState(AssistantState.BrowserControl);

        // Act
        controller.StopAll();

        // Assert
        Assert.Equal(AssistantState.Off, controller.CurrentState);
    }

    [Fact]
    public void StopAll_SetsIsStoppedToTrue()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);

        // Act
        controller.StopAll();

        // Assert
        Assert.True(controller.IsStopped);
    }

    [Fact]
    public void StopAll_CancelsRuntimeToken()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);
        var token = controller.RuntimeToken;

        // Act
        controller.StopAll();

        // Assert
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void StopAll_RaisesStopAllTriggeredEvent()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);
        var eventRaised = false;
        controller.StopAllTriggered += (_, _) => eventRaised = true;

        // Act
        controller.StopAll();

        // Assert
        Assert.True(eventRaised);
    }

    [Fact]
    public void StopAll_LogsToAuditLog()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);

        // Act
        controller.StopAll();

        // Assert
        var events = _auditLogger.ReadTail(10);
        var stopEvent = events.Last();
        
        Assert.Equal("STOP_ALL", stopEvent.Action);
        Assert.Equal("user", stopEvent.Actor);
        Assert.Equal("ok", stopEvent.Result);
    }

    [Fact]
    public void SetState_ReturnsFalseAfterStopAll()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);
        controller.StopAll();

        // Act
        var result = controller.SetState(AssistantState.Listening);

        // Assert
        Assert.False(result);
        Assert.Equal(AssistantState.Off, controller.CurrentState);
    }

    [Fact]
    public void SetState_LogsRejectionAfterStopAll()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);
        controller.StopAll();

        // Act
        controller.SetState(AssistantState.Thinking);

        // Assert
        var events = _auditLogger.ReadTail(10);
        var rejection = events.Last();
        
        Assert.Equal("STATE_CHANGE_REJECTED", rejection.Action);
        Assert.Equal("denied", rejection.Result);
    }

    [Fact]
    public void StopAll_IsIdempotent()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);
        controller.StopAll();
        var eventCountAfterFirst = _auditLogger.ReadTail(100).Count;

        // Act
        controller.StopAll();
        controller.StopAll();

        // Assert
        var eventCountAfterMultiple = _auditLogger.ReadTail(100).Count;
        Assert.Equal(eventCountAfterFirst, eventCountAfterMultiple);
    }

    [Fact]
    public void Reset_AllowsStateTransitionsAgain()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);
        controller.StopAll();
        Assert.True(controller.IsStopped);

        // Act
        controller.Reset();

        // Assert
        Assert.False(controller.IsStopped);
        Assert.Equal(AssistantState.Idle, controller.CurrentState);
        
        var result = controller.SetState(AssistantState.Listening);
        Assert.True(result);
    }

    [Fact]
    public void Reset_ProvidesNewRuntimeToken()
    {
        // Arrange
        using var controller = new RuntimeController(_auditLogger);
        var originalToken = controller.RuntimeToken;
        controller.StopAll();
        Assert.True(originalToken.IsCancellationRequested);

        // Act
        controller.Reset();

        // Assert
        var newToken = controller.RuntimeToken;
        Assert.False(newToken.IsCancellationRequested);
    }
}
