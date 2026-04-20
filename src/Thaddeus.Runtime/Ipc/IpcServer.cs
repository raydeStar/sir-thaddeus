using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Thaddeus.Runtime.Events;
using Thaddeus.Runtime.Hosting;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Ipc;

/// <summary>
/// Hosts the shell ↔ runtime IPC channel. Wire format is newline-delimited JSON
/// (<see cref="IpcMessage"/>). Each accepted client gets a long-lived connection and
/// receives every event from the bus mirrored over IPC.
/// </summary>
public sealed class IpcServer : BackgroundService
{
    /// <summary>JSON serialization options used for IPC frames.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RuntimeOptions _options;
    private readonly IEventBus _bus;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<IpcServer> _logger;

    /// <summary>Wires the IPC server.</summary>
    public IpcServer(
        RuntimeOptions options,
        IEventBus bus,
        IHostApplicationLifetime lifetime,
        ILogger<IpcServer> logger)
    {
        _options = options;
        _bus = bus;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ipc.server.starting endpoint={Endpoint}", _options.IpcEndpoint);

        try
        {
            if (IpcEndpoint.IsWindows)
            {
                await RunNamedPipeServerAsync(_options.IpcEndpoint, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await RunUnixSocketServerAsync(_options.IpcEndpoint, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ipc.server.crashed");
            throw;
        }
    }

    private async Task RunNamedPipeServerAsync(string pipeName, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 4,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                break;
            }

            // Spin off the connection so the listener loop can accept the next client.
            _ = HandleConnectionAsync(server, stoppingToken);
        }
    }

    private async Task RunUnixSocketServerAsync(string path, CancellationToken stoppingToken)
    {
        // Stale UDS files block bind. Spec §6.1 expects the runtime to clean these up.
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }

        var endpoint = new UnixDomainSocketEndPoint(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(endpoint);
        listener.Listen(backlog: 4);

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch { /* best effort */ }

        while (!stoppingToken.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await listener.AcceptAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var stream = new NetworkStream(accepted, ownsSocket: true);
            _ = HandleConnectionAsync(stream, stoppingToken);
        }

        try { File.Delete(path); } catch { /* best effort */ }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken stoppingToken)
    {
        var connectionCt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var writer = Channel.CreateUnbounded<IpcMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // Mirror runtime events to this client.
        using var subscription = _bus.Subscribe(async (evt, ct) =>
        {
            await writer.Writer.WriteAsync(new IpcMessage
            {
                Id = evt.Id,
                Type = "event",
                Payload = evt,
            }, ct).ConfigureAwait(false);
        });

        var sender = Task.Run(() => SendLoopAsync(stream, writer.Reader, connectionCt.Token), connectionCt.Token);
        try
        {
            await ReadLoopAsync(stream, writer.Writer, connectionCt.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ipc.client.disconnected");
        }
        finally
        {
            writer.Writer.TryComplete();
            connectionCt.Cancel();
            try { await sender.ConfigureAwait(false); } catch { /* drain */ }
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(Stream stream, ChannelWriter<IpcMessage> outbound, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return; // remote closed
            if (string.IsNullOrWhiteSpace(line)) continue;

            IpcMessage? msg;
            try
            {
                msg = JsonSerializer.Deserialize<IpcMessage>(line, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ipc.parse_failed line={Line}", line);
                continue;
            }
            if (msg is null) continue;

            var response = await HandleMessageAsync(msg, ct).ConfigureAwait(false);
            if (response is not null)
            {
                await outbound.WriteAsync(response, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<IpcMessage?> HandleMessageAsync(IpcMessage message, CancellationToken ct)
    {
        switch (message.Type)
        {
            case "hello":
                return HandleHello(message);

            case "ping":
                return new IpcMessage { Id = message.Id, Type = "pong" };

            case "shutdown":
                _logger.LogInformation("ipc.shutdown_requested by_shell=true");
                _lifetime.StopApplication();
                return new IpcMessage { Id = message.Id, Type = "shutdown.ack" };

            default:
                return new IpcMessage
                {
                    Id = message.Id,
                    Type = "error",
                    Error = new IpcError { Code = "E_UNKNOWN_TYPE", Message = $"Unknown message type '{message.Type}'." },
                };
        }
    }

    private IpcMessage HandleHello(IpcMessage message)
    {
        // Spec §6.1: the shell handshakes with a version-check message. We accept a
        // minimum version supplied by the shell (or, in v1, exact match).
        string? shellVersion = null;
        if (message.Payload is JsonElement je && je.TryGetProperty("version", out var v))
        {
            shellVersion = v.GetString();
        }

        var compatible = string.IsNullOrEmpty(shellVersion) || string.Equals(shellVersion, _options.Version, StringComparison.Ordinal);
        if (!compatible)
        {
            _logger.LogWarning("ipc.version_mismatch shell={Shell} runtime={Runtime}", shellVersion, _options.Version);
            return new IpcMessage
            {
                Id = message.Id,
                Type = "hello.ack",
                Payload = new HelloAckPayload(_options.Version, false),
                Error = new IpcError
                {
                    Code = "E_VERSION_MISMATCH",
                    Message = $"Shell ({shellVersion}) and runtime ({_options.Version}) versions do not match.",
                },
            };
        }

        return new IpcMessage
        {
            Id = message.Id,
            Type = "hello.ack",
            Payload = new HelloAckPayload(_options.Version, true),
        };
    }

    private static async Task SendLoopAsync(Stream stream, ChannelReader<IpcMessage> reader, CancellationToken ct)
    {
        await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var msg))
            {
                var json = JsonSerializer.Serialize(msg, JsonOptions);
                await writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Payload of a <c>hello.ack</c> response.</summary>
    /// <param name="RuntimeVersion">Runtime semver.</param>
    /// <param name="Compatible">Whether the runtime considers the shell compatible.</param>
    public sealed record HelloAckPayload(string RuntimeVersion, bool Compatible);
}
