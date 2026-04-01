# Move MCP Tool Sources Into Package Tree

- Status: Done
- Branch: `task/move-mcp-tool-sources`
- Started: 2026-04-01
- Objective: Move MCP tool source files out of the app-layer `apps/mcp-server/.../Tools` directory and into the package projects that already own their compilation so tool ownership, discovery, and build responsibility are aligned.
- Selection Basis: Next repo-grounded cleanup slice from `06-risk-and-bloat-report.md` after launcher consolidation and SearXNG decoupling, supported by the linked-include duplication finding in `04-dead-code-and-duplication.md`.

## Phase 1

- Selected as the narrowest high-value cleanup step after `t11-launcher-helper`.
- Evidence:
  - `06-risk-and-bloat-report.md`: identifies MCP tool ownership clean-up as the next structural cleanup after launcher consolidation and sidecar decoupling.
  - `04-dead-code-and-duplication.md`: documents the linked-include MCP tool source pattern as a high-confidence ownership and maintenance problem.
  - Current project structure compiles tool sources from the app tree into package assemblies via linked includes, which obscures authoritative ownership.

## Notes

- Keep scope narrow: move files and update project ownership only.
- Do not refactor tool behavior while relocating sources.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Moved the core MCP tool sources from `apps/mcp-server/SirThaddeus.McpServer/Tools` into `packages/mcp-tools-core/SirThaddeus.McpTools.Core/Tools`.
- Moved the Windows-only MCP tool sources into `packages/mcp-tools-windows/SirThaddeus.McpTools.Windows/Tools`.
- Removed the linked-source compile wiring from the core and Windows MCP tool package project files.
- Removed the now-obsolete app-layer `Compile Remove="Tools\**\*.cs"` workaround from the MCP server project because the tool sources no longer live in the app project tree.

## Phase 3

- Solution build: `dotnet build SirThaddeus.sln --no-restore -c Release`
  - Result: passed with 0 errors.
- Solution tests: `dotnet test SirThaddeus.sln -c Release --no-build`
  - Result: 2101 passed, 0 failed.

## Phase 4

- Confidence: 100%
- Reason: This task only relocates MCP tool source ownership to the package projects that already expose the tool assemblies, with no behavioral refactor. The full Release build and full Release test suite both passed unchanged.