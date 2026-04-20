using Thaddeus.Runtime.Hosting;

namespace Thaddeus.Runtime.Tests;

public sealed class AuthFailureTrackerTests
{
    [Fact]
    public void Lockout_engages_after_threshold_failures()
    {
        var tracker = new AuthFailureTracker { FailureThreshold = 5, Window = TimeSpan.FromSeconds(60), LockoutDuration = TimeSpan.FromSeconds(30) };
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 4; i++)
        {
            Assert.False(tracker.RecordFailure(now));
        }
        Assert.True(tracker.RecordFailure(now), "Fifth failure should engage lockout.");
        Assert.True(tracker.IsLockedOut(now));
        Assert.False(tracker.IsLockedOut(now + TimeSpan.FromSeconds(31)));
    }

    [Fact]
    public void Old_failures_outside_window_are_ignored()
    {
        var tracker = new AuthFailureTracker { FailureThreshold = 5, Window = TimeSpan.FromSeconds(60), LockoutDuration = TimeSpan.FromSeconds(30) };
        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 4; i++)
        {
            tracker.RecordFailure(t0);
        }
        // 61 seconds later: a single new failure should not trip lockout because the
        // earlier four are now outside the sliding window.
        Assert.False(tracker.RecordFailure(t0 + TimeSpan.FromSeconds(61)));
    }

    [Fact]
    public void Success_resets_count()
    {
        var tracker = new AuthFailureTracker { FailureThreshold = 5 };
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 4; i++) tracker.RecordFailure(now);
        tracker.RecordSuccess();
        Assert.False(tracker.RecordFailure(now));
    }
}
