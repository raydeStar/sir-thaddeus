using SirThaddeus.Config;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Execution;

/// <summary>
/// v2 hybrid-runtime adapter. Stub today — exists so
/// <see cref="HarnessHostFactory"/> can dispatch on
/// <c>--target v2</c> and so the architectural seam is real.
///
/// <para>
/// To make this functional, the following pieces need wiring (each is
/// an isolated change; this list is the punchlist for the next session):
/// </para>
/// <list type="number">
///   <item>
///     <b>Sandbox shape.</b>
///     <see cref="HarnessRuntimeSandbox.CreateShared"/> writes a
///     v1-shaped <c>settings.json</c>. v2 needs its own paths
///     (memos dir, threads dir, voice cache, knowledge store roots).
///     Add <c>CreateSharedV2(AppSettings)</c> or fold a
///     <c>HostTarget</c> parameter into <c>CreateInternal</c>.
///   </item>
///   <item>
///     <b>Process spawn.</b>
///     v2's assembly is
///     <c>src/Thaddeus.Runtime/bin/Debug/net10.0/Thaddeus.Runtime.dll</c>.
///     Add a <c>ResolveHybridRuntimeAssembly()</c> sibling to the v1
///     resolver and pass v2's CLI args (note: v2 may need a different
///     port flag and probably no <c>--tools</c>).
///   </item>
///   <item>
///     <b>Chat shape.</b>
///     v2 uses <c>POST /api/threads</c> + <c>POST /api/threads/{id}/messages</c>
///     plus a <c>/ws</c> WebSocket for streaming events, instead of v1's
///     <c>POST /api/chat</c> + SSE. Implement
///     <c>RunChatAsync</c> by creating a thread, posting the message,
///     opening a WS, and consuming until <c>turn.completed</c>.
///   </item>
///   <item>
///     <b>Permission auto-approve.</b>
///     v2 emits <c>permission.request</c> events on <c>/ws</c>; respond
///     via <c>POST /api/permissions/respond</c>. Replace v1's
///     <c>/api/permissions/{id}/decision</c> path.
///   </item>
///   <item>
///     <b>Tool trace reconstruction.</b>
///     v1 reads JSONL audit log (<c>/api/audit?take=N</c>). v2 emits
///     activity log entries (<c>/api/activity</c>) and richer WS events.
///     Pick one canonical source per host and adapt
///     <c>BuildToolTraceFromAudit</c> equivalent here.
///   </item>
///   <item>
///     <b>Memo wipe.</b>
///     v2's <c>HarnessApi</c> currently returns rows=0 on
///     <c>ClearMemoryData</c>. Inject <c>IMemoStore</c> and add a
///     <c>WipeAllAsync()</c> when this adapter starts running tests
///     that rely on a clean memory state.
///   </item>
///   <item>
///     <b>Personality activation.</b>
///     v2 stores active personality in settings rather than a dedicated
///     endpoint. Either write <c>PUT /api/settings</c> per test or add a
///     v2 <c>POST /api/personalities/active</c> shim for parity.
///   </item>
/// </list>
///
/// <para>
/// The seam is intentional: each item above is a small, separately
/// reviewable change. The interface
/// (<see cref="IHarnessHostAdapter"/>) is identical for v1 and v2 so
/// the harness orchestrator, scoring, artifact writer, and iteration
/// engine never need to know which host is plugged in.
/// </para>
/// </summary>
internal sealed class HybridRuntimeHostAdapter : IHarnessHostAdapter
{
    public HybridRuntimeHostAdapter(AppSettings _)
    {
        throw new NotImplementedException(
            "Hybrid (v2) harness adapter is not wired yet. " +
            "Run with --target v1 (default) for now. " +
            "See the class summary in HybridRuntimeHostAdapter.cs for the punchlist.");
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task<HostExecutionResult> ExecuteAsync(
        HarnessTestCase test,
        CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
