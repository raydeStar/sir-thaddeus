using System.Collections.Concurrent;
using System.Threading.Channels;
using SirThaddeus.Agent;
using SirThaddeus.Contracts;

internal static partial class RuntimeApiServer
{
    internal sealed class RunState
    {
        private readonly object _gate = new();
        private readonly List<RuntimeEventEnvelope> _history = [];
        private readonly List<ChannelWriter<RuntimeEventEnvelope>> _subscribers = [];
        private readonly CancellationTokenSource _cancellation = new();
        private IReadOnlyList<ToolCallRecord> _harnessToolEvidence = [];
        private bool _completed;

        public RunState(string runId)
        {
            RunId = runId;
        }

        public string RunId { get; }
        public CancellationToken CancellationToken => _cancellation.Token;

        public void Cancel() => _cancellation.Cancel();

        public void SetHarnessToolEvidence(IReadOnlyList<ToolCallRecord> toolCalls)
        {
            ArgumentNullException.ThrowIfNull(toolCalls);
            lock (_gate)
            {
                _harnessToolEvidence = toolCalls.Select(call => call with { }).ToArray();
            }
        }

        public IReadOnlyList<ToolCallRecord> GetHarnessToolEvidence()
        {
            lock (_gate)
            {
                return _harnessToolEvidence.Select(call => call with { }).ToArray();
            }
        }

        public void Complete()
        {
            List<ChannelWriter<RuntimeEventEnvelope>> subscribers;
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                subscribers = [.. _subscribers];
                _subscribers.Clear();
            }

            foreach (var subscriber in subscribers)
            {
                subscriber.TryComplete();
            }
        }

        public void Append(string eventType, object payload)
        {
            var envelope = new RuntimeEventEnvelope(eventType, RunId, DateTimeOffset.UtcNow, payload);
            List<ChannelWriter<RuntimeEventEnvelope>> subscribers;
            lock (_gate)
            {
                _history.Add(envelope);
                subscribers = [.. _subscribers];
            }

            foreach (var subscriber in subscribers)
            {
                subscriber.TryWrite(envelope);
            }
        }

        public async IAsyncEnumerable<RuntimeEventEnvelope> StreamEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ChannelReader<RuntimeEventEnvelope>? reader = null;
            List<RuntimeEventEnvelope> replay;
            ChannelWriter<RuntimeEventEnvelope>? writer = null;

            lock (_gate)
            {
                replay = [.. _history];
                if (!_completed)
                {
                    var channel = Channel.CreateUnbounded<RuntimeEventEnvelope>();
                    writer = channel.Writer;
                    reader = channel.Reader;
                    _subscribers.Add(writer);
                }
            }

            try
            {
                foreach (var evt in replay)
                {
                    yield return evt;
                }

                if (reader is null)
                {
                    yield break;
                }

                await foreach (var evt in reader.ReadAllAsync(cancellationToken))
                {
                    yield return evt;
                }
            }
            finally
            {
                if (writer is not null)
                {
                    lock (_gate)
                    {
                        _subscribers.Remove(writer);
                    }
                }
            }
        }
    }
}
