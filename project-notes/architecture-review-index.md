# Architecture Review Index

This is the review-first map of architecture docs in this repository.
If you are reviewing design decisions, read in this order.

## Recommended review order

1. `project-notes/architecture-nuts-bolts.md`
2. `project-notes/mcp-tools-reference.md`
3. `project-notes/tool-conflict-matrix.md`
4. `project-notes/tool-routing-v2.md`
5. `project-notes/s2s-stack-arch-plan.md`
6. `project-notes/architectural-design.md`

## Document catalog

| Document | Scope | Status | Notes |
|---|---|---|---|
| `project-notes/architecture-nuts-bolts.md` | Runtime architecture, trust boundary, process/wiring details | Current | Best source for "what runs where" |
| `project-notes/mcp-tools-reference.md` | MCP tool contracts, permission model, audit guarantees | Current | Operational reference for tool behavior |
| `project-notes/tool-conflict-matrix.md` | Deterministic turn-level tool conflict resolution | Current | Applied before MCP execution |
| `project-notes/tool-routing-v2.md` | Router -> policy gate -> executor flow, MCP hooks | Current | Implementation-focused notes |
| `project-notes/s2s-stack-arch-plan.md` | Push-to-talk voice stack design and constraints | Active plan | Voice architecture and UX contract |
| `project-notes/architectural-design.md` | Product-level architecture strategy and long-range direction | Strategy | Higher-level than runtime wiring |

## Related technical docs

| Document | Scope |
|---|---|
| `project-notes/voice-engine-setup.md` | Voice backend setup and local model configuration |
| `project-notes/web-search.md` | Web search subsystem behavior and tuning notes |
| `project-notes/testing-memory.md` | Memory pipeline diagnostics and testing procedures |

## Review checklist (fast)

- One explicit trust boundary: side effects happen only through MCP tools
- One universal permission enforcement point for MCP calls
- Audit coverage for every tool call start/end
- Deterministic policy gate before tool exposure
- Tool conflict behavior documented and testable
- Voice path and lifecycle documented (PTT, ASR, TTS, cancellation)
