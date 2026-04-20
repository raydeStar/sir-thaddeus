using Thaddeus.Runtime.Activity;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public class InMemoryActivityLogTests
{
    [Fact]
    public void Append_then_List_returns_newest_first()
    {
        var log = new InMemoryActivityLog();
        var a = log.Append(NewEntry("a"));
        var b = log.Append(NewEntry("b"));
        var c = log.Append(NewEntry("c"));

        var listed = log.List(10);
        Assert.Equal(new[] { c.Id, b.Id, a.Id }, listed.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Append_evicts_oldest_when_capacity_exceeded()
    {
        var log = new InMemoryActivityLog(capacity: 2);
        var a = log.Append(NewEntry("a"));
        var b = log.Append(NewEntry("b"));
        var c = log.Append(NewEntry("c"));

        var listed = log.List(10);
        Assert.Equal(2, listed.Count);
        Assert.Equal(new[] { c.Id, b.Id }, listed.Select(e => e.Id).ToArray());
        Assert.Null(log.Get(a.Id));
    }

    [Fact]
    public void Update_changes_status_and_completed_and_returns_new_entry()
    {
        var log = new InMemoryActivityLog();
        var entry = log.Append(NewEntry("running"));
        var done = DateTimeOffset.UtcNow;

        var updated = log.Update(entry.Id, status: ActivityStatus.Ok, completedAt: done, detail: "ran fine");

        Assert.NotNull(updated);
        Assert.Equal(ActivityStatus.Ok, updated!.Status);
        Assert.Equal(done, updated.CompletedAt);
        Assert.Equal("ran fine", updated.Detail);
        Assert.Same(updated, log.Get(entry.Id) is { } got && got == updated ? updated : null!);
        // Get returns the updated entry too.
        Assert.Equal(ActivityStatus.Ok, log.Get(entry.Id)!.Status);
    }

    [Fact]
    public void Update_returns_null_for_missing_or_evicted_entry()
    {
        var log = new InMemoryActivityLog(capacity: 1);
        var first = log.Append(NewEntry("first"));
        log.Append(NewEntry("second")); // evicts first

        var result = log.Update(first.Id, status: ActivityStatus.Ok);
        Assert.Null(result);

        Assert.Null(log.Update("act_nope", status: ActivityStatus.Ok));
    }

    [Fact]
    public void Changed_event_fires_on_append_and_update()
    {
        var log = new InMemoryActivityLog();
        var seen = new List<string>();
        log.Changed += e => seen.Add($"{e.Id}:{e.Status}");

        var entry = log.Append(NewEntry("x"));
        log.Update(entry.Id, status: ActivityStatus.Ok);

        Assert.Equal(2, seen.Count);
        Assert.Contains(seen, s => s.EndsWith(":Running", StringComparison.Ordinal));
        Assert.Contains(seen, s => s.EndsWith(":Ok", StringComparison.Ordinal));
    }

    private static ActivityEntry NewEntry(string summary) => new(
        Id: InMemoryActivityLog.NewId(),
        Kind: ActivityKind.ChatTurn,
        Summary: summary,
        Status: ActivityStatus.Running,
        StartedAt: DateTimeOffset.UtcNow,
        CompletedAt: null,
        ThreadId: null,
        Detail: null);
}
