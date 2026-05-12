using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Thaddeus.Runtime.Events;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Hosted service that subscribes to <see cref="IEventBus"/> and tees every
/// turn-scoped event to a per-message JSONL file under <c>&lt;lockDir&gt;/turns</c>.
///
/// <para>The chat-turn events already flow through the bus to the WebSocket
/// broadcaster (so the live UI animates). They are otherwise ephemeral —
/// nothing else durably captures the routing decision, the tool calls, the
/// search-provider diagnostics, or the final assistant text in a single,
/// turn-keyed place. This writer fills that gap so a future user complaint
/// of "this answer was awful" can be answered with a single file read keyed
/// off the assistant <c>messageId</c>.</para>
///
/// <para>Streaming token deltas (<see cref="ChatTurnEvents.Delta"/>) are
/// intentionally excluded — the assembled text is on
/// <see cref="ChatTurnEvents.Complete"/>, and persisting every chunk would
/// inflate trace files by 30–100x with no diagnostic value.</para>
///
/// <para>Path layout: <c>&lt;TurnsRoot&gt;/&lt;messageId&gt;.jsonl</c>, one JSON
/// object per line, append-only. Concurrency is safe because the bus
/// serialises handler invocations.</para>
/// </summary>
public sealed class TurnTraceWriter : IHostedService, IDisposable
{
    private static readonly HashSet<string> TrackedTypes = new(StringComparer.Ordinal)
    {
        ChatTurnEvents.Start,
        ChatTurnEvents.Complete,
        ChatTurnEvents.ToolStarted,
        ChatTurnEvents.ToolCompleted,
        ChatTurnEvents.FootmanDecision,
        ChatTurnEvents.MemoryRecalled,
        ChatTurnEvents.UserMessageAppended,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly IEventBus _bus;
    private readonly ILogger<TurnTraceWriter> _logger;
    private readonly string _root;
    private IDisposable? _subscription;

    public TurnTraceWriter(IEventBus bus, ILogger<TurnTraceWriter> logger, string root)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root path is required.", nameof(root));
        _root = root;
    }

    /// <summary>Absolute directory the writer persists trace files under.</summary>
    public string Root => _root;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_root);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "turn.trace.create_root_failed root={Root}", _root);
        }

        _subscription = _bus.Subscribe(HandleAsync);
        _logger.LogDebug("turn.trace.writer.started root={Root}", _root);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();

    private async Task HandleAsync(RuntimeEvent<object?> evt, CancellationToken ct)
    {
        if (!TrackedTypes.Contains(evt.Type)) return;

        var messageId = evt.CorrelationId;
        if (string.IsNullOrWhiteSpace(messageId) || !IsSafeFileName(messageId))
            return;

        // Each line is the full RuntimeEvent envelope so a reader can match
        // the event to what the WebSocket broadcaster sent the UI without
        // any reshaping.
        string line;
        try
        {
            line = JsonSerializer.Serialize(evt, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "turn.trace.serialize_failed type={Type}", evt.Type);
            return;
        }

        var path = Path.Combine(_root, messageId + ".jsonl");
        try
        {
            await File.AppendAllTextAsync(path, line + "\n", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "turn.trace.write_failed messageId={MessageId}", messageId);
        }
    }

    /// <summary>
    /// Defensive filename guard: reject any correlation id that contains
    /// characters which could escape the turns directory (path separators,
    /// dots, drive letters, …). ULIDs and our message ids are alphanumeric +
    /// hyphen/underscore so this never rejects a real id.
    /// </summary>
    private static bool IsSafeFileName(string s)
    {
        if (s.Length == 0 || s.Length > 128) return false;
        foreach (var c in s)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }
        return true;
    }
}
