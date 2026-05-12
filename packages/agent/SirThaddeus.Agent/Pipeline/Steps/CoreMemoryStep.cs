using System.Text;
using SirThaddeus.LlmClient;
using SirThaddeus.Memory;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that injects a small "core memory" block at the end of
/// the system prompt on every chat turn, unconditionally. Unlike
/// <see cref="MemoryContextStep"/>, this is NOT keyed off the current
/// user message — it always carries the user's display name plus the
/// top user-pinned <see cref="MemoryNugget"/> entries.
///
/// <para>Industry-standard agent-memory pattern (MemGPT/Letta, Mem0,
/// Claude/ChatGPT memory): a thin always-in-context tier of high-value
/// facts that should never need to be re-discovered (name, role, hard
/// preferences), with the larger dynamic retrieval tier handling
/// situation-specific recall. This step is the always-in-context tier.</para>
///
/// <para><b>Scoping:</b> only nuggets with <c>PinLevel &gt;= 1</c> qualify.
/// Level 1 is user-pinned (via the /memory audit UI); level 2 is reserved
/// for system promotions and isn't reachable from the UI today. Items are
/// ordered by pin level desc, then use_count desc, then updated_at desc.</para>
///
/// <para><b>Budget:</b> hard-capped by <paramref>maxBytes</paramref> so a
/// runaway pin count never drowns the system prompt. Default is 1 KB —
/// enough for ~8 short nuggets plus the profile line.</para>
///
/// <para><b>Fail-open:</b> any failure (DB unavailable, no profile, zero
/// pinned items) leaves the prompt untouched. The turn never fails because
/// core memory couldn't load.</para>
///
/// <para>Place this step <b>after</b> <see cref="MemoryContextStep"/> so
/// the dynamic [REMEMBERED CONTEXT] block lands before the static
/// [CORE MEMORY] block — the model reads them in natural order from
/// situational to stable.</para>
/// </summary>
public sealed class CoreMemoryStep : ITurnStep
{
    private const int DefaultMaxBytes = 1024;
    private const int CandidatePoolSize = 100;

    private readonly IMemoryStore? _store;
    private readonly int _maxBytes;

    /// <param name="store">Direct memory store handle. Null = step is a
    /// no-op (runtimes without a wired-up memory stack pass through).</param>
    /// <param name="maxBytes">Byte cap for the assembled block. Items are
    /// added in priority order and the first one that would push past the
    /// cap is dropped (and so are all subsequent items).</param>
    public CoreMemoryStep(IMemoryStore? store, int maxBytes = DefaultMaxBytes)
    {
        _store = store;
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
    }

    public string Name => "CoreMemory";

    public async Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_store is null)
            return new StepResult.Continue(context);

        string? block;
        try
        {
            block = await BuildCoreBlockAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fail-open. Core memory is a nice-to-have; a DB hiccup must
            // not derail the turn.
            return new StepResult.Continue(context);
        }

        if (string.IsNullOrWhiteSpace(block))
            return new StepResult.Continue(context);

        var updated = AppendCoreBlockToSystemMessage(context.LlmMessages, block);
        return new StepResult.Continue(context with { LlmMessages = updated });
    }

    private async Task<string?> BuildCoreBlockAsync(CancellationToken ct)
    {
        // Read the user profile (display name + structured fields) and a
        // small candidate pool of nuggets. The pool is filtered client-
        // side to PinLevel >= 1 — small enough that the extra rows don't
        // matter even on heavily-used installs.
        var profile = await _store!.GetUserProfileAsync(ct).ConfigureAwait(false);

        var pinned = await _store
            .ListPinnedNuggetsAsync(CandidatePoolSize, ct)
            .ConfigureAwait(false);

        if (profile is null && pinned.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.Append("What I remember about you:\n");

        if (profile is not null && !string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            var line = $"- Preferred name: {profile.DisplayName}\n";
            if (Encoding.UTF8.GetByteCount(line) <= _maxBytes - Encoding.UTF8.GetByteCount(sb.ToString()))
                sb.Append(line);
        }

        foreach (var nugget in pinned)
        {
            var text = (nugget.Text ?? string.Empty).Trim();
            if (text.Length == 0) continue;

            var line = $"- {text}\n";
            var remaining = _maxBytes - Encoding.UTF8.GetByteCount(sb.ToString());
            if (Encoding.UTF8.GetByteCount(line) > remaining)
                break;

            sb.Append(line);
        }

        var assembled = sb.ToString().TrimEnd();
        // If we only wrote the header (nothing else fit or qualified),
        // skip the injection entirely rather than confuse the model with
        // an empty rubric.
        return assembled.Contains('\n') ? assembled : null;
    }

    private static IReadOnlyList<ChatMessage> AppendCoreBlockToSystemMessage(
        IReadOnlyList<ChatMessage> messages,
        string block)
    {
        // Wrap with explicit tags so small models recognise the section
        // as durable user context rather than baseline instructions. Same
        // pattern MemoryContextStep uses for its dynamic block.
        var formatted = "\n\n[CORE MEMORY]\n" + block + "\n[/CORE MEMORY]";

        for (var i = 0; i < messages.Count; i++)
        {
            if (!string.Equals(messages[i].Role, "system", StringComparison.OrdinalIgnoreCase))
                continue;

            var combined = (messages[i].Content ?? string.Empty) + formatted;
            var next = messages.ToArray();
            next[i] = ChatMessage.System(combined);
            return next;
        }

        var inserted = new List<ChatMessage>(messages.Count + 1) { ChatMessage.System(formatted.TrimStart()) };
        inserted.AddRange(messages);
        return inserted;
    }
}
