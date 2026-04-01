# Extract Message Surface Handler From MainWindow

- Status: Done
- Branch: `task/extract-message-surface-handler`
- Started: 2026-04-01
- Objective: Extract the last-response actions, per-message actions, attachment helpers, and source-card helpers from `MainWindow.axaml.cs` into a dedicated partial class so the ongoing MainWindow decomposition continues through a cohesive message-surface subsystem.
- Selection Basis: Next repo-grounded cleanup slice after `t19-conversation-handler`, based on the remaining MainWindow responsibility clusters and the fact that message copy/retry/read-aloud, source display, and attachment handling already operate as one operator-facing workflow around chat content.

## Phase 1

- Selected as the next narrow verifiable slice of the broader MainWindow decomposition work.
- Evidence:
  - The cluster is cohesive around message-surface interactions: last-assistant actions, per-message context menu actions, attachment handling, and source-card projection helpers.
  - The cluster is bounded away from the core prompt submission and runtime event-stream logic.
  - The extraction is behavior-preserving because the methods already operate through existing MainWindow state, controls, and helper calls without requiring a new abstraction.

## Notes

- Keep scope narrow: move only the message-surface, attachment, and source-card helpers.
- Do not redesign chat UX, audit UX, or prompt submission behavior in this task.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Added `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.MessageSurface.cs` and moved the message-surface workflow into it.
- Moved last-assistant actions, per-message context menu actions, attachment handling, and source-card helper methods out of `MainWindow.axaml.cs`.
- Kept behavior unchanged by retaining the existing MainWindow state, shared control bindings, and helper calls, while deliberately leaving audit file open handlers and the core prompt/composer path in place.

## Phase 3

- Build:
  - `dotnet build SirThaddeus.sln --no-restore -c Release`
  - Result: passed with 0 errors.
- Tests:
  - `Remove-Item Env:ST_AUDIT_PATH -ErrorAction SilentlyContinue; Remove-Item Env:ST_SETTINGS_PATH -ErrorAction SilentlyContinue; dotnet test SirThaddeus.sln -c Release --no-build`
  - Result: 2101 passed, 0 failed, 0 skipped.

## Phase 4

- Confidence: 100%.
- Reason: the extraction stayed narrowly focused on message-surface behavior, compiled cleanly on the first pass, and the full solution test suite passed without regression.