# Observability

Every Sir Thaddeus component routes its logs through the shared
`SirThaddeus.Logging` module so operators have one place to look when
something breaks. This document is the contract: where logs go, how to
read them, and how to extend the system.

## Where logs land

Every component writes a rolling daily log file under:

```
%LocalAppData%\SirThaddeus\logs\{component}\{component}-YYYYMMDD.log
```

On Windows, `%LocalAppData%` is `C:\Users\<you>\AppData\Local`. The same
`LocalApplicationData` convention is honored on macOS and Linux; on those
platforms the path resolves to `~/.local/share/SirThaddeus/logs/...` or
equivalent.

Component names currently wired:

| Component             | Entry point                                     |
| --------------------- | ----------------------------------------------- |
| `headless-runtime`    | `apps/headless-runtime/.../Program.cs`          |
| `voice-host`          | `apps/voice-host/.../Program.cs`                |
| `mcp-server`          | `apps/mcp-server/.../Program.cs`                |
| `ui-avalonia`         | `apps/ui-avalonia/.../Program.cs`               |

Files roll at midnight UTC and at 32 MB. Fourteen files are retained per
component; older files are deleted automatically.

## Format

File logs are written in Serilog's **Compact JSON** format — one JSON
object per line. This is intentional: a human can still grep the files,
but every line is also machine-parseable, which matters for post-mortem
analysis of a running installation.

```json
{"@t":"2026-04-19T14:32:08.123Z","@l":"Warning","@m":"Backend proxy 502 from http://127.0.0.1:17845","@x":"...stack...","Component":"voice-host","RequestId":"a1b2c3"}
```

Every log line is automatically enriched with the `Component` field, so
merged logs from multiple components remain disambiguated.

Console logs are human-readable, single-line, with a short level tag
(`INF`, `WRN`, `ERR`). The `mcp-server` routes its console output to
**stderr only**, because stdout is the MCP stdio transport and must
stay clean for the protocol.

## Log levels

- **Verbose / Debug** — expected events, including "best-effort" failures
  we intentionally tolerate (UI elements disappearing mid-walk, favicon
  fetch failures, cleanup paths during teardown). These are not errors;
  they are breadcrumbs for when the surrounding behavior looks wrong.
- **Information** — state transitions that matter operationally (startup,
  session begin/end, backend process spawned).
- **Warning** — the user-facing experience degraded but the system kept
  running (voice settings failed to load and we fell back to defaults;
  a retry succeeded; a readiness probe reported "not ready").
- **Error** — an operation the user asked for failed. A stack is expected.
- **Fatal** — the process is about to exit because of this.

The default minimum level is **Information**. Raise verbosity without
editing code by setting the `SIRTHADDEUS_LOG_LEVEL` environment variable
to one of `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`.

## Using the logger in new code

For a component that runs under `IHostApplicationBuilder` (web hosts,
generic hosts):

```csharp
using SirThaddeus.Logging;

var builder = WebApplication.CreateBuilder(args);
builder.UseSirThaddeusLogging(new LoggingOptions
{
    ComponentName = "my-component",
});
```

Downstream code takes `ILogger<T>` as usual; the MEL abstraction flows
through the Serilog provider transparently.

For a component **without** a host (e.g. the Avalonia UI):

```csharp
using Serilog;
using SirThaddeus.Logging;

Log.Logger = LoggingBootstrap.BuildSerilogLogger(new LoggingOptions
{
    ComponentName = "my-component",
});
AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.CloseAndFlush();
```

Downstream code then uses `Serilog.Log.ForContext<T>().Information(...)`
(or `.Debug`, `.Warning`, etc.) to emit contextualized messages. Any
package that wants to log adds a `PackageReference Include="Serilog"` to
its csproj — no project reference to `SirThaddeus.Logging` is needed
unless the package is doing its own bootstrap.

## Policy: no silent catch blocks

Catches that *intentionally* suppress an exception still need to emit a
log line. The pattern is:

```csharp
try { DoThing(); }
catch (Exception ex)
{
    Log.ForContext<MyClass>().Debug(ex, "Thing failed but is non-fatal");
}
```

The severity level reflects whether downstream behavior is affected:

- **Debug** — cleanup paths, tolerated failures (the caller can't tell).
- **Warning** — user-observable degradation occurred as a result.
- **Error / Fatal** — operation failed; escalate.

Pure re-throw catches (`catch { throw; }`) and narrowly-scoped
cancellation paths (`catch (OperationCanceledException) { throw; }`)
don't need a log line — they're not suppressing anything.

## Reading logs during triage

The fastest triage path:

```powershell
# Tail the latest log for a component
Get-Content "$env:LOCALAPPDATA\SirThaddeus\logs\voice-host\voice-host-*.log" -Tail 200

# Pipe through jq-style parsing to find warnings across components
Get-ChildItem "$env:LOCALAPPDATA\SirThaddeus\logs\*\*.log" |
  Get-Content | ConvertFrom-Json |
  Where-Object { $_.'@l' -in 'Warning','Error','Fatal' }
```

Because every component writes the same JSON shape, the merged output
is coherent even when multiple processes are active.
