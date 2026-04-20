using System.IO.Pipes;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Thaddeus.SharedTypes;

namespace Thaddeus.Shell.Ipc;

/// <summary>
/// Shell-side IPC client. Connects to the runtime over named pipe (Windows) or Unix
/// domain socket (POSIX), performs the hello handshake, and exposes a fire-and-forget
/// channel plus event delivery to subscribers.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    /// <summary>JSON serialisation options for IPC frames. Mirrors the runtime side.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<IpcClient> _logger;
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readLoopCts;

    /// <summary>Initialises the client.</summary>
    public IpcClient(ILogger<IpcClient> logger)
    {
        _logger = logger;
    }

    /// <summary>Raised when the runtime sends an event.</summary>
    public event Action<RuntimeEvent<JsonElement>>? EventReceived;

    /// <summary>Connects and performs the hello handshake. Throws on version mismatch.</summary>
    public async Task ConnectAndHandshakeAsync(string ipcEndpoint, CancellationToken ct, string? shellVersionOverride = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(ipcEndpoint);

        _stream = await OpenStreamAsync(ipcEndpoint, ct).ConfigureAwait(false);
        _reader = new StreamReader(_stream, leaveOpen: true);
        _writer = new StreamWriter(_stream, leaveOpen: true) { AutoFlush = true };

        var shellVersion = shellVersionOverride
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var helloId = Guid.NewGuid().ToString("N");
        var hello = new IpcMessage
        {
            Id = helloId,
            Type = "hello",
            Payload = new { version = shellVersion },
        };
        await SendAsync(hello, ct).ConfigureAwait(false);

        // Read frames until we see the hello.ack matching helloId. Buffer any earlier
        // event frames so the subscriber doesn't lose them once the read loop starts.
        var buffered = new List<IpcMessage>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await _reader!.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Runtime closed IPC before hello.ack.");
            var msg = JsonSerializer.Deserialize<IpcMessage>(line, JsonOptions);
            if (msg is null) continue;

            if (msg.Type == "hello.ack" && msg.Id == helloId)
            {
                if (msg.Error is { Code: "E_VERSION_MISMATCH" })
                {
                    throw new IpcVersionMismatchException(msg.Error.Message);
                }
                break;
            }
            buffered.Add(msg);
        }

        _readLoopCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(buffered, _readLoopCts.Token), _readLoopCts.Token);
    }

    /// <summary>Sends a fire-and-forget message.</summary>
    public async Task SendAsync(IpcMessage message, CancellationToken ct)
    {
        if (_writer is null) throw new InvalidOperationException("IPC client is not connected.");
        var json = JsonSerializer.Serialize(message, JsonOptions);
        await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
    }

    /// <summary>Sends a <c>shutdown</c> message and waits briefly for the ack.</summary>
    public async Task RequestShutdownAsync(CancellationToken ct)
    {
        try
        {
            await SendAsync(new IpcMessage { Id = Guid.NewGuid().ToString("N"), Type = "shutdown" }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ipc.shutdown_send_failed");
        }
    }

    private async Task ReadLoopAsync(List<IpcMessage> buffered, CancellationToken ct)
    {
        try
        {
            foreach (var msg in buffered) Dispatch(msg);

            while (!ct.IsCancellationRequested && _reader is not null)
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                IpcMessage? msg;
                try { msg = JsonSerializer.Deserialize<IpcMessage>(line, JsonOptions); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ipc.parse_failed line={Line}", line);
                    continue;
                }
                if (msg is null) continue;
                Dispatch(msg);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ipc.read_loop_failed");
        }
    }

    private void Dispatch(IpcMessage msg)
    {
        if (msg.Type != "event" || msg.Payload is null) return;
        try
        {
            // Round-trip through JSON to materialise the inner payload as JsonElement.
            var json = JsonSerializer.Serialize(msg.Payload, JsonOptions);
            var evt = JsonSerializer.Deserialize<RuntimeEvent<JsonElement>>(json, JsonOptions);
            if (evt is not null) EventReceived?.Invoke(evt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ipc.dispatch_failed");
        }
    }

    private static async Task<Stream> OpenStreamAsync(string endpoint, CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout: 5000, ct).ConfigureAwait(false);
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), ct).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try { _readLoopCts?.Cancel(); } catch { /* best effort */ }
        _readLoopCts?.Dispose();
        if (_writer is not null) await _writer.DisposeAsync().ConfigureAwait(false);
        _reader?.Dispose();
        if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Thrown when the runtime declines the shell's version during hello handshake.</summary>
public sealed class IpcVersionMismatchException : Exception
{
    /// <summary>Initialises the exception.</summary>
    public IpcVersionMismatchException(string message) : base(message) { }
}
