# Contributing

Thanks for contributing to Sir Thaddeus.

This project is local-first, permissioned, and safety-oriented. Please keep changes deterministic, testable, and auditable.

## Ground Rules

- Keep behavior explicit. No hidden background autonomy.
- Preserve user control: approvals, time-boxed permissions, and STOP semantics.
- Prefer small, reviewable pull requests.
- Add or update tests with code changes.

## Dev Setup

Prerequisites:

- Windows
- .NET SDK (see repo tooling output via `dotnet --info`)
- PowerShell 5.1+

From repo root:

```powershell
./dev/bootstrap.ps1
```

Run locally:

```powershell
./dev/localrunner.ps1
```

Useful modes:

- `./dev/localrunner.ps1 --debug` for separate backend windows/log visibility
- `./dev/preflight.ps1` for release-style verification

## Package Structure

Use this layout when deciding where new code belongs:

- `packages/agent` — orchestration, routing, guardrails, workflow
- `packages/core` — cross-cutting primitives and utilities
- `packages/*` — reusable libraries grouped by capability
- `apps/mcp-server` — MCP host and tool entry points
- `packages/mcp-tools-core` — cross-platform MCP tool assembly
- `packages/mcp-tools-windows` — Windows-only MCP tools (screen/clipboard)
- `tests/SirThaddeus.Tests` and `tests/SirThaddeus.Windows.Tests` — unit/integration coverage

Prefer extending an existing package before creating a new one.

## Testing

Run full test suite:

```powershell
./dev/test_all.ps1
```

Run focused tests:

```powershell
./dev/test.ps1 -Filter "FullyQualifiedName~SirThaddeus.Tests.Voice"
```

If your change touches routing, voice, permissions, or tool execution, include targeted tests for regressions.

## Code Style

- Follow existing project conventions and naming.
- Keep changes minimal and local to the problem.
- Prefer deterministic logic over prompt-only behavior where safety is involved.
- Do not commit secrets, local credentials, or private machine paths in logs.

## Pull Request Checklist

Before opening a PR:

1. Rebase/merge latest `master`.
2. Run relevant tests and include results in PR description.
3. Update docs for user-visible behavior changes (`README.md`, `SECURITY.md`, etc.).
4. Note risk areas and rollback plan for non-trivial changes.

PR description should include:

- What changed
- Why it changed
- How it was tested
- Any known limitations

## Adding a New MCP Tool

1. Add the tool method in `apps/mcp-server/SirThaddeus.McpServer/Tools/`.
2. Keep `[McpTool]` metadata clear and action-oriented (name, description, parameter docs).
3. Apply permission tiering and policy mapping in `packages/agent` (`ToolGroupPolicy`, tool-name constants).
4. Add/adjust manifest entries in `packages/mcp-shared` so capability listing stays complete.
5. Add tests under `tests/SirThaddeus.Tests/MCP` (and Windows tests when platform-specific).
6. Validate with:
	- `dotnet build SirThaddeus.sln -c Release`
	- `dotnet test SirThaddeus.sln -c Release`
	- `./dev/harness.ps1 --suite smoke --max-iters 1 --judge none`

## Adding a New Package

1. Create folder `packages/<capability>/SirThaddeus.<Capability>/`.
2. Use project name and root namespace `SirThaddeus.<Capability>`.
3. Add the project to `SirThaddeus.sln` under the Packages solution folder.
4. Add only required project references; avoid dependency cycles.
5. Add matching tests in `tests/SirThaddeus.Tests/<Capability>/`.
6. Update docs/settings templates when the package introduces user-visible behavior.

## Security Issues

For vulnerabilities, do not open a full public issue first.

Use the security reporting path in [SECURITY.md](SECURITY.md).
