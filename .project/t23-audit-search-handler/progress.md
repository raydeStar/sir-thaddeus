# Extract Audit Search Handler From MainWindow

- Status: Done
- Branch: `task/extract-audit-search-handler`
- Started: 2026-04-01
- Objective: Move the audit and search diagnostics controls from `MainWindow.axaml.cs` into a dedicated partial class so the ongoing MainWindow decomposition continues through a cohesive diagnostics subsystem.
- Selection Basis: Next repo-grounded cleanup slice after `t22-profiles-admin-handler`, based on the remaining MainWindow responsibility clusters and the fact that search status refresh, audit refresh, audit log open actions, and audit line formatting all belong to the same settings-side diagnostics workflow.

## Phase 1

- Selected as the next narrow verifiable slice of the broader MainWindow decomposition work.
- Evidence:
  - The cluster is cohesive around diagnostics: live search status, audit list refresh, audit log file/folder open actions, and the shared audit-line formatting helper.
  - The cluster is already called from settings and runtime-connection flows, so extracting it into one partial reduces reliance on the monolithic code-behind without changing behavior.
  - The extraction is behavior-preserving because the methods already operate through existing MainWindow state, controls, and runtime API calls without requiring a new abstraction.

## Notes

- Keep scope narrow: move only the search/audit diagnostics controls and their local helper.
- Do not redesign the diagnostics UX or runtime API contracts in this task.
- Do not touch unrelated untracked planning documents at repo root.

## Phase 2

- Added `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.Diagnostics.cs`.
- Moved the diagnostics cluster out of `MainWindow.axaml.cs`:
  - `RefreshSearchStatusButton_Click`
  - `RefreshSearchStatusAsync`
  - `RefreshAuditButton_Click`
  - `RefreshAuditAsync`
  - `ToAuditLine`
  - `OpenAuditLogFile_Click`
  - `OpenAuditLogFolder_Click`
- Kept `ResolveThemeBrush` and unrelated helpers in `MainWindow.axaml.cs`.
- One compile fix was required after extraction: add `using SirThaddeus.Contracts;` for `AuditEntryDto` in the new partial.

## Phase 3

- Build command:
  - `dotnet build SirThaddeus.sln --no-restore -c Release`
- Result:
  - First attempt failed with 1 error: missing `AuditEntryDto` namespace in `MainWindow.Diagnostics.cs`.
  - Second attempt passed with 0 errors.

- Test command:
  - `Remove-Item Env:ST_AUDIT_PATH -ErrorAction SilentlyContinue; Remove-Item Env:ST_SETTINGS_PATH -ErrorAction SilentlyContinue; dotnet test SirThaddeus.sln -c Release --no-build`
- Result:
  - 2101 passed, 0 failed, 0 skipped.

## Phase 4

- Confidence: 100%
- Reason: the diagnostics seam was extracted without widening scope, the Release build passes, and the full solution test suite passed without regressions.