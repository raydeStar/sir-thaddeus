using SirThaddeus.Core;

namespace SirThaddeus.Tests;

/// <summary>
/// Tests for AssistantState enum and extensions.
/// </summary>
public class AssistantStateTests
{
    [Theory]
    [InlineData(AssistantState.Off, "Off")]
    [InlineData(AssistantState.Idle, "Idle")]
    [InlineData(AssistantState.Listening, "Listening...")]
    [InlineData(AssistantState.Thinking, "Thinking...")]
    [InlineData(AssistantState.ReadingScreen, "Reading Screen")]
    [InlineData(AssistantState.BrowserControl, "Browser Control")]
    [InlineData(AssistantState.ServiceWorking, "Service Working")]
    public void ToDisplayLabel_ReturnsCorrectLabel(AssistantState state, string expectedLabel)
    {
        // Act
        var label = state.ToDisplayLabel();

        // Assert
        Assert.Equal(expectedLabel, label);
    }

    [Theory]
    [InlineData(AssistantState.Off, "power_off")]
    [InlineData(AssistantState.Idle, "check_circle")]
    [InlineData(AssistantState.Listening, "mic")]
    [InlineData(AssistantState.Thinking, "hourglass")]
    [InlineData(AssistantState.ReadingScreen, "visibility")]
    [InlineData(AssistantState.BrowserControl, "mouse")]
    [InlineData(AssistantState.ServiceWorking, "cloud")]
    public void ToIconHint_ReturnsCorrectIcon(AssistantState state, string expectedIcon)
    {
        // Act
        var icon = state.ToIconHint();

        // Assert
        Assert.Equal(expectedIcon, icon);
    }

    [Fact]
    public void AllStatesHaveDisplayLabels()
    {
        // Arrange
        var allStates = Enum.GetValues<AssistantState>();

        // Act & Assert
        foreach (var state in allStates)
        {
            var label = state.ToDisplayLabel();
            Assert.False(string.IsNullOrEmpty(label), $"State {state} has no display label");
        }
    }

    [Fact]
    public void AllStatesHaveIconHints()
    {
        // Arrange
        var allStates = Enum.GetValues<AssistantState>();

        // Act & Assert
        foreach (var state in allStates)
        {
            var icon = state.ToIconHint();
            Assert.False(string.IsNullOrEmpty(icon), $"State {state} has no icon hint");
            Assert.NotEqual("help", icon); // "help" is the fallback
        }
    }
}
