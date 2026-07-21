using SirThaddeus.Agent.Search;

namespace SirThaddeus.Tests.Agent.Search;

public sealed class CurrentDateTimeUtilityTests
{
    [Theory]
    [InlineData("Could you show me today's date?", "date")]
    [InlineData("Please give me the current day.", "date")]
    [InlineData("I need to know what day it is today.", "date")]
    [InlineData("Would you tell me the current local time, please?", "time")]
    [InlineData("Do you have the time?", "time")]
    [InlineData("Display the correct time right now.", "time")]
    public void Current_local_requests_match(string prompt, string expectedCategory)
    {
        var match = DeterministicUtilityEngine.TryMatch(prompt);

        Assert.NotNull(match);
        Assert.Equal(expectedCategory, match!.Result.Category);
    }

    [Theory]
    [InlineData("What is the time difference between Boise and Lisbon?")]
    [InlineData("What time does the train from Salem arrive?")]
    [InlineData("What time is it in Nairobi?")]
    [InlineData("Convert 3 PM Eastern to Pacific time.")]
    [InlineData("What date is Easter in 2027?")]
    [InlineData("Show me tomorrow's calendar appointments.")]
    [InlineData("Remind me at the current time every weekday.")]
    [InlineData("What is the time for the quarterly meeting?")]
    public void Non_current_requests_do_not_take_inline_datetime_path(string prompt)
    {
        var match = DeterministicUtilityEngine.TryMatch(prompt);

        Assert.True(
            match is null ||
            (match.Result.Category != "date" && match.Result.Category != "time"),
            $"Unexpected inline current-date/time activation for: {prompt}");
    }
}
