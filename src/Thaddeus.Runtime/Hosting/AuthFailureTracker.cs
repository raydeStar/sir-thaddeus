using System.Collections.Concurrent;

namespace Thaddeus.Runtime.Hosting;

/// <summary>
/// Tracks failed bearer-auth attempts. Spec §6.3 mandates a brief lockout after
/// 5 failures within 60 seconds, plus a structured log entry. The log entry is
/// emitted by the middleware that owns this tracker; this type only owns the policy.
/// </summary>
public sealed class AuthFailureTracker
{
    private readonly object _lock = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private DateTimeOffset _lockoutUntil = DateTimeOffset.MinValue;

    /// <summary>Maximum failures permitted in <see cref="Window"/> before lockout engages.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>Sliding window over which failures are counted.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Duration of the lockout once <see cref="FailureThreshold"/> is reached.</summary>
    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>True if the runtime should refuse all auth attempts right now.</summary>
    public bool IsLockedOut(DateTimeOffset now)
    {
        lock (_lock)
        {
            return now < _lockoutUntil;
        }
    }

    /// <summary>Records a failed auth attempt and returns true if the new failure tripped a lockout.</summary>
    public bool RecordFailure(DateTimeOffset now)
    {
        lock (_lock)
        {
            _failures.Enqueue(now);
            var threshold = now - Window;
            while (_failures.Count > 0 && _failures.Peek() < threshold)
            {
                _failures.Dequeue();
            }

            if (_failures.Count >= FailureThreshold)
            {
                _lockoutUntil = now + LockoutDuration;
                _failures.Clear();
                return true;
            }
            return false;
        }
    }

    /// <summary>Resets state. Called on a successful auth.</summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failures.Clear();
        }
    }
}
