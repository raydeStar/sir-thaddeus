using SirThaddeus.AuditLog;
using SirThaddeus.Memory;

namespace Thaddeus.Runtime.Memory;

/// <summary>
/// Deterministic memory consolidation. Today scope: dedupe facts whose
/// normalized <c>(Subject, Predicate, Object)</c> triple matches another
/// non-deleted fact.
///
/// <para>This is the **manual, deterministic** version of the reflection
/// pass from the unified-memory plan. There's intentionally NO automatic
/// scheduling, no LLM-driven summarization, and no soft-delete of
/// non-duplicates in this iteration — those carry product risk
/// (auto-deleting things a user might want) and need real usage data
/// before we tune.</para>
///
/// <para><b>Safety contract:</b></para>
/// <list type="bullet">
///   <item>Only acts on facts whose <c>(Subject, Predicate, Object)</c>
///   triple — case-insensitive, trimmed — exactly matches another non-
///   deleted fact. A typo in any of the three keeps both alive.</item>
///   <item>Within a duplicate group, keeps the highest-confidence fact
///   (ties broken by earliest <c>CreatedAt</c>) and soft-deletes the rest
///   via <see cref="IMemoryStore.DeleteFactAsync"/>. Soft-delete is the
///   store's contract; nothing is purged.</item>
///   <item>Hard cap of 100 deletions per pass. If a user has a runaway
///   duplicate explosion, they get one pass at a time and can review
///   the audit log between passes.</item>
///   <item>Every deletion lands in the audit log as
///   <c>"memory_reflection.deduped_fact"</c> with the kept-id + dropped-id
///   so an admin can restore from the SQLite DB if needed.</item>
/// </list>
/// </summary>
public sealed class MemoryReflectionService
{
    private const int MaxDeletionsPerPass = 100;
    private const int FactScanLimit = 10_000;

    private readonly IMemoryStore _store;
    private readonly IAuditLogger _audit;
    private readonly ILogger<MemoryReflectionService> _logger;

    public MemoryReflectionService(
        IMemoryStore store,
        IAuditLogger audit,
        ILogger<MemoryReflectionService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs one reflection pass. Always returns a report; never throws on
    /// store errors (logs + reports zero work instead). Cancellation is
    /// respected promptly.
    /// </summary>
    public async Task<ReflectionReport> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<MemoryFact> facts;
        try
        {
            var (items, _) = await _store
                .ListFactsAsync(filter: null, skip: 0, take: FactScanLimit, cancellationToken)
                .ConfigureAwait(false);
            facts = items;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "memory.reflection.list_facts_failed");
            sw.Stop();
            return new ReflectionReport(
                StartedAt: startedAt,
                FactsScanned: 0,
                DuplicateGroups: 0,
                FactsRemoved: 0,
                DurationMs: sw.ElapsedMilliseconds,
                Actions: Array.Empty<ReflectionAction>(),
                Error: $"List facts failed: {ex.Message}");
        }

        // Group by the normalized triple. Empty / whitespace components
        // are excluded from grouping — they can't deterministically be
        // called duplicates of each other.
        var groups = facts
            .Where(f => !string.IsNullOrWhiteSpace(f.Subject)
                     && !string.IsNullOrWhiteSpace(f.Predicate)
                     && !string.IsNullOrWhiteSpace(f.Object))
            .GroupBy(f => new
            {
                S = f.Subject.Trim().ToLowerInvariant(),
                P = f.Predicate.Trim().ToLowerInvariant(),
                O = f.Object.Trim().ToLowerInvariant()
            })
            .Where(g => g.Count() > 1)
            .ToList();

        var actions = new List<ReflectionAction>();
        var removed = 0;

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (removed >= MaxDeletionsPerPass) break;

            var ordered = group
                .OrderByDescending(f => f.Confidence)
                .ThenBy(f => f.CreatedAt)
                .ToList();

            var keeper = ordered[0];
            foreach (var dup in ordered.Skip(1))
            {
                if (removed >= MaxDeletionsPerPass) break;

                try
                {
                    await _store.DeleteFactAsync(dup.MemoryId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "memory.reflection.delete_failed factId={FactId}", dup.MemoryId);
                    actions.Add(new ReflectionAction(
                        Kind: "delete_skipped",
                        FactId: dup.MemoryId,
                        Reason: $"delete failed: {ex.Message}",
                        KeptFactId: keeper.MemoryId,
                        Subject: dup.Subject,
                        Predicate: dup.Predicate,
                        Object: dup.Object));
                    continue;
                }

                actions.Add(new ReflectionAction(
                    Kind: "deduped_fact",
                    FactId: dup.MemoryId,
                    Reason: $"duplicate of {keeper.MemoryId} (kept higher confidence / earlier)",
                    KeptFactId: keeper.MemoryId,
                    Subject: dup.Subject,
                    Predicate: dup.Predicate,
                    Object: dup.Object));

                // Audit trail. Failure to write the audit row mustn't
                // mask the delete — we already removed the duplicate.
                try
                {
                    await _audit.AppendAsync(new AuditEvent
                    {
                        Actor = "runtime.memory_reflection",
                        Action = "memory_reflection.deduped_fact",
                        Target = dup.MemoryId,
                        Result = "ok",
                        Details = new Dictionary<string, object>
                        {
                            ["keptFactId"] = keeper.MemoryId,
                            ["subject"] = dup.Subject,
                            ["predicate"] = dup.Predicate,
                            ["object"] = dup.Object,
                        }
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "memory.reflection.audit_failed factId={FactId}", dup.MemoryId);
                }

                removed++;
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "memory.reflection.complete scanned={Scanned} groups={Groups} removed={Removed} durationMs={DurationMs}",
            facts.Count, groups.Count, removed, sw.ElapsedMilliseconds);

        return new ReflectionReport(
            StartedAt: startedAt,
            FactsScanned: facts.Count,
            DuplicateGroups: groups.Count,
            FactsRemoved: removed,
            DurationMs: sw.ElapsedMilliseconds,
            Actions: actions,
            Error: null);
    }
}

/// <summary>Result of a single reflection pass.</summary>
public sealed record ReflectionReport(
    DateTimeOffset StartedAt,
    int FactsScanned,
    int DuplicateGroups,
    int FactsRemoved,
    long DurationMs,
    IReadOnlyList<ReflectionAction> Actions,
    string? Error);

/// <summary>One action taken during reflection (or skipped with reason).</summary>
public sealed record ReflectionAction(
    /// <summary>"deduped_fact" on success, "delete_skipped" on failure.</summary>
    string Kind,
    string FactId,
    string Reason,
    string KeptFactId,
    string Subject,
    string Predicate,
    string Object);
