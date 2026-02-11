# Deployment Guide

This guide defines a repeatable production deployment workflow for the desktop runtime.

## 1) Preflight gate (required)

Run this before creating any release artifact:

```powershell
.\dev\preflight.ps1
```

What it does:

- validates local environment/restore via `.\dev\bootstrap.ps1`
- runs the full Release test suite via `.\dev\test_all.ps1`

If preflight fails, do not package or distribute.

## 2) Publish release artifacts

Publish both desktop runtime and MCP server into the same output directory.
The desktop runtime can then discover the MCP server automatically.

```powershell
$out = ".\artifacts\publish\win-x64"

dotnet publish .\apps\mcp-server\SirThaddeus.McpServer\SirThaddeus.McpServer.csproj `
  -c Release -r win-x64 --self-contained false -o $out

dotnet publish .\apps\desktop-runtime\SirThaddeus.DesktopRuntime\SirThaddeus.DesktopRuntime.csproj `
  -c Release -r win-x64 --self-contained false -o $out
```

Expected output examples:

- `SirThaddeus.DesktopRuntime.exe`
- `SirThaddeus.McpServer.exe`
- supporting `.dll` files for both runtimes

## 3) Smoke test checklist

Run from the publish output folder:

```powershell
.\SirThaddeus.DesktopRuntime.exe --headless
```

Verify:

1. LLM connection status is healthy.
2. MCP tools are discoverable (non-zero tool count).
3. Chat response works for:
   - normal prompt
   - memory-aware personalization prompt
4. No internal markers appear in user-visible output (for example, tool/reference markers).
5. No unsupported capability claims appear (for example, email/send promises when no such tool exists).
6. Audit log continues to append entries in `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`.

## 4) Packaging and release handoff

Recommended:

1. Zip the full publish directory.
2. Include:
   - release notes
   - pinned SDK/runtime notes
   - checksum/hash for the archive
3. Keep the previous known-good package available for rollback.

## 5) Post-deploy checks

After rollout on a clean machine/profile:

- start app, confirm tray + command palette behavior
- confirm memory DB initialization at `%LOCALAPPDATA%\SirThaddeus\memory.db`
- run one end-to-end query and verify tool activity/audit events
- confirm shutdown/restart behavior and settings persistence
