using System.Text.Json.Serialization;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.State;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// Chat REST endpoints (Phase 3.2). Read/write operations on threads + messages.
/// Streaming assistant turns ride on the existing /ws channel (Phase 3.3).
/// </summary>
public static class ChatApi
{
    /// <summary>Registers <c>/api/threads</c> and <c>/api/threads/{id}/...</c> endpoints.</summary>
    public static void MapChatApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/threads", async (IThreadStore store, CancellationToken ct) =>
        {
            var threads = await store.ListAsync(ct).ConfigureAwait(false);
            return Results.Json(
                new ThreadListResponse(threads.Select(ThreadSummary.From).ToArray()),
                ChatJsonContext.Default.ThreadListResponse);
        })
            .WithName("ListThreads");

        app.MapPost("/api/threads", async (CreateThreadRequest? req, IThreadStore store, CancellationToken ct) =>
        {
            var title = req?.Title ?? string.Empty;
            var thread = await store.CreateAsync(title, ct).ConfigureAwait(false);
            return Results.Json(thread, ChatJsonContext.Default.ChatThread, statusCode: StatusCodes.Status201Created);
        })
            .WithName("CreateThread");

        app.MapGet("/api/threads/{id}", async (string id, IThreadStore store, CancellationToken ct) =>
        {
            var thread = await store.GetAsync(id, ct).ConfigureAwait(false);
            return thread is null
                ? Results.NotFound()
                : Results.Json(thread, ChatJsonContext.Default.ChatThread);
        })
            .WithName("GetThread");

        app.MapDelete("/api/threads/{id}", async (string id, IThreadStore store, CancellationToken ct) =>
        {
            var ok = await store.DeleteAsync(id, ct).ConfigureAwait(false);
            return ok ? Results.NoContent() : Results.NotFound();
        })
            .WithName("DeleteThread");

        app.MapPatch("/api/threads/{id}", async (string id, PatchThreadRequest? req, IThreadStore store,
            CancellationToken ct) =>
        {
            if (req is null) return Results.BadRequest(new { error = "body required" });

            ChatThread? current = null;
            if (req.Title is not null)
            {
                current = await store.RenameAsync(id, req.Title, ct).ConfigureAwait(false);
                if (current is null) return Results.NotFound();
            }
            if (req.Pinned.HasValue)
            {
                current = await store.SetPinnedAsync(id, req.Pinned.Value, ct).ConfigureAwait(false);
                if (current is null) return Results.NotFound();
            }
            if (current is null)
            {
                // No-op patch: return the existing thread if it exists.
                current = await store.GetAsync(id, ct).ConfigureAwait(false);
                if (current is null) return Results.NotFound();
            }
            return Results.Json(current, ChatJsonContext.Default.ChatThread);
        })
            .WithName("PatchThread");

        app.MapPost("/api/threads/{id}/messages",
            async (string id, AppendMessageRequest? req, IThreadStore store, StubAssistant assistant,
                RuntimeStateMachine machine, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            var message = new ChatMessage(
                Id: NewMessageId(),
                Role: ChatRole.User,
                Text: req.Text.Trim(),
                CreatedAt: DateTimeOffset.UtcNow);

            try
            {
                var updated = await store.AppendMessageAsync(id, message, ct).ConfigureAwait(false);

                // Project chat lifecycle onto the runtime state machine so the shell's
                // status badge animates Idle -> Thinking -> Idle for typed turns.
                // Illegal transitions (e.g. user sends a second message mid-reply) are
                // logged and discarded by the machine; chat persistence still succeeds.
                machine.TryTransition(StateTrigger.UserTextSubmitted);

                // Kick off the stub assistant on a background task. The HTTP caller
                // returns immediately with the user message; the assistant reply is
                // streamed over /ws and persisted to the store when complete.
                _ = Task.Run(async () =>
                {
                    var log = loggerFactory.CreateLogger("ChatApi.AssistantTurn");
                    try
                    {
                        await assistant.RespondAsync(id, message.Text, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "stub_assistant.respond_failed thread={ThreadId}", id);
                    }
                    finally
                    {
                        // Stub assistant is text-only; close the loop back to Idle.
                        machine.TryTransition(StateTrigger.PlanTextOnly);
                    }
                });

                return Results.Json(
                    new AppendMessageResponse(message, updated),
                    ChatJsonContext.Default.AppendMessageResponse,
                    statusCode: StatusCodes.Status201Created);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        })
            .WithName("AppendMessage");
    }

    private static string NewMessageId() =>
        "msg_" + Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 8)).ToLowerInvariant();
}

/// <summary>Body for POST /api/threads.</summary>
public sealed record CreateThreadRequest(string? Title);

/// <summary>Body for PATCH /api/threads/{id}. Both fields are optional.</summary>
public sealed record PatchThreadRequest(string? Title, bool? Pinned);

/// <summary>Body for POST /api/threads/{id}/messages.</summary>
public sealed record AppendMessageRequest(string Text);

/// <summary>Response for POST /api/threads/{id}/messages.</summary>
public sealed record AppendMessageResponse(ChatMessage Message, ChatThread Thread);

/// <summary>Compact thread summary for the chat list view.</summary>
public sealed record ThreadSummary(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? LastMessagePreview,
    bool Pinned)
{
    public static ThreadSummary From(ChatThread t)
    {
        var last = t.Messages.Count > 0 ? t.Messages[^1].Text : null;
        var preview = last is null ? null : (last.Length > 140 ? last[..140] : last);
        return new ThreadSummary(t.Id, t.Title, t.CreatedAt, t.UpdatedAt, t.Messages.Count, preview, t.Pinned);
    }
}

/// <summary>Response wrapper for GET /api/threads.</summary>
public sealed record ThreadListResponse(IReadOnlyList<ThreadSummary> Threads);

/// <summary>Source-generated JSON context for chat payloads.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ChatThread))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ThreadListResponse))]
[JsonSerializable(typeof(ThreadSummary))]
[JsonSerializable(typeof(PatchThreadRequest))]
[JsonSerializable(typeof(CreateThreadRequest))]
[JsonSerializable(typeof(AppendMessageRequest))]
[JsonSerializable(typeof(AppendMessageResponse))]
public partial class ChatJsonContext : JsonSerializerContext
{
}
