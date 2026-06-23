using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Execution;

/// <summary>
/// Abstraction over a runtime that the harness can drive end-to-end.
/// The harness orchestrator (<see cref="HarnessApplication"/>,
/// <see cref="SingleTestRunner"/>) only knows about this seam — concrete
/// hosts plug in behind it.
///
/// <para>
/// Today there's exactly one implementation,
/// <see cref="HeadlessRuntimeHarnessClient"/> for the v1 headless host.
/// The v2 hybrid runtime adapter is the next planned implementation; a
/// stub lives in <c>HybridRuntimeHostAdapter</c>. Both will satisfy the
/// same interface so the harness can flip targets via a CLI flag without
/// touching scoring, artifact writing, or iteration logic.
/// </para>
///
/// <para>
/// Why this seam matters: the UI and the harness should be peers, not
/// asymmetric clients. When both go through the same chat-shaped HTTP
/// surface, an E2E suite written against the harness exercises exactly
/// the path a real user hits in the browser. That's the
/// "Claude Code / Claude App" parity goal — same brain, two clients.
/// </para>
/// </summary>
internal interface IHarnessHostAdapter : IAsyncDisposable
{
    /// <summary>
    /// One-time idempotent setup before any tests run. Concrete hosts may
    /// no-op here and lazy-init inside <see cref="ExecuteAsync"/> instead.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs a single harness test against the host: spawns the runtime if
    /// it isn't already running, applies per-test reset, sends the user
    /// message, streams the run to completion, and returns the response
    /// plus a reconstructed tool trace and timing breakdown.
    ///
    /// <para>
    /// Implementations are expected to reuse the runtime process across
    /// calls and only pay startup cost once per harness invocation.
    /// </para>
    /// </summary>
    Task<HostExecutionResult> ExecuteAsync(
        HarnessTestCase test,
        CancellationToken cancellationToken);
}
