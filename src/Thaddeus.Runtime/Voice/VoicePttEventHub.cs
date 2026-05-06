using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Thaddeus.Runtime.Voice;

public sealed class VoicePttEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<VoicePttEvent>> _subscribers = new();

    public VoicePttSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<VoicePttEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _subscribers[id] = channel;
        return new VoicePttSubscription(id, channel.Reader, () => Unsubscribe(id));
    }

    public void Publish(string phase, string source)
    {
        var evt = new VoicePttEvent(
            Phase: phase,
            Source: source,
            AtUtc: DateTimeOffset.UtcNow);

        foreach (var (id, channel) in _subscribers.ToArray())
        {
            if (!channel.Writer.TryWrite(evt))
                Unsubscribe(id);
        }
    }

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }
}

public sealed record VoicePttEvent(string Phase, string Source, DateTimeOffset AtUtc);

public sealed class VoicePttSubscription : IAsyncDisposable
{
    private readonly Action _dispose;
    private bool _disposed;

    public VoicePttSubscription(Guid id, ChannelReader<VoicePttEvent> reader, Action dispose)
    {
        Id = id;
        Reader = reader;
        _dispose = dispose;
    }

    public Guid Id { get; }

    public ChannelReader<VoicePttEvent> Reader { get; }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _dispose();
        return ValueTask.CompletedTask;
    }
}
