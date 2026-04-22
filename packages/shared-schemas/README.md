# Shared Schemas

JSON Schema source-of-truth for cross-runtime types.
Both the .NET runtime/shell and the React workspace consume types **generated** from
these schemas. Hand-maintained parallel definitions are forbidden (per spec §22.1, §25).

## Schemas

| File | Purpose |
| --- | --- |
| `runtime-state.schema.json` | Top-level runtime state enum (§7.1) |
| `runtime-state-event.schema.json` | Detailed `runtime.state` event payload (§7.2) |
| `runtime-event.schema.json` | Generic envelope for all events on the WebSocket bus (§18.2) |
| `ipc-message.schema.json` | NDJSON envelope for shell ↔ runtime IPC (§6.2) |
| `lock-file.schema.json` | `~/.thaddeus/runtime.lock` contents (§6.1, §6.3) |

## Generation

Generation is wired up in Phase 1.2 (runtime project). Until then the C# and TS
versions in `packages/shared-types/` are minimal hand-written placeholders that
mirror these schemas exactly. Phase 1.2 will replace them with generated output
and wire the regeneration into the build.
