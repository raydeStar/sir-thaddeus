# Runtime IPC Contract (Hybrid Runtime Draft)

Date: 2026-03-05  
Status: Draft for the hybrid runtime web client

## Scope

This contract defines the UI <-> runtime boundary.  
The UI is a client only: it renders state, sends user prompts, and returns permission decisions.

## Transport

- HTTP for commands and snapshots.
- SSE (or WebSocket) for run event streaming.
- Local host binding only: `127.0.0.1`.

## Endpoints

### `POST /api/chat`

Starts a run.

Request body:

```json
{
  "prompt": "Find me flights to Denver",
  "conversationId": "optional",
  "sessionId": "optional"
}
```

Response:

```json
{
  "runId": "run_123",
  "startedAtUtc": "2026-03-05T20:00:00Z"
}
```

### `POST /api/runs/{runId}/cancel`

Requests cancellation.

Request body:

```json
{
  "reason": "user_stop"
}
```

Response:

```json
{
  "runId": "run_123",
  "accepted": true
}
```

### `GET /api/runs/{runId}/events`

Streams run events via SSE (default) or WebSocket.

SSE event payload envelope:

```json
{
  "eventType": "token.delta",
  "runId": "run_123",
  "timestampUtc": "2026-03-05T20:00:01Z",
  "payload": {}
}
```

### `GET /api/audit`

Returns audit entries for UI log view.

### `GET /api/health`

Returns runtime status and version.

### `POST /api/permissions/{requestId}/decision`

Submits an operator decision for a pending tool request.

Request body:

```json
{
  "approved": true
}
```

Response:

```json
{
  "requestId": "abc123",
  "applied": true
}
```

## Event types

- `token.delta`: incremental output token chunk.
- `run.completed`: final output and completion metadata.
- `run.failed`: error or cancellation terminal event.
- `tool.requested`: permission required with tool metadata.
- `tool.approved`: operator approved tool execution.
- `tool.denied`: operator denied tool execution.
- `audit.appended`: audit record appended.

## Permission flow

1. Runtime emits `tool.requested` with request id, tool name, reason, arguments.
2. UI presents approve/deny.
3. UI posts decision to runtime via `POST /api/permissions/{requestId}/decision`.
4. Runtime emits `tool.approved` or `tool.denied`.

## Shared DTO package

Shared IPC DTOs are defined in:

- `packages/contracts/SirThaddeus.Contracts`

Current files:

- `ChatContracts.cs`
- `EventsContracts.cs`
- `AuditContracts.cs`
