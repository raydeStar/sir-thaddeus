# Extract Workflow Progress Handler From MainWindow

- Status: Done
- Branch: `task/extract-workflow-progress-handler`
- Started: 2026-04-01
- Objective: Move the workflow progress drawer and tool-strip state helpers out of `MainWindow.axaml.cs` into a dedicated partial so event-stream-driven workflow UI state has a focused home.
- Selection Basis: Next narrow verifiable slice after `t23-audit-search-handler`, based on the remaining `MainWindow.axaml.cs` responsibility clusters and the fact that the progress drawer reset/show/hide/auto-collapse logic plus workflow tool-strip formatting already operate as one cohesive workflow-progress subsystem.

## Phase 1

- Selected as the next low-risk seam because it is referenced heavily by `MainWindow.EventStream.cs` but does not require touching the core prompt submission/runtime launch path.
- Evidence:
  - `ResetWorkflowProgressUi`, `ShowProgressDrawer`, `HideProgressDrawer`, `AutoCollapseProgressDrawerAsync`, `CancelProgressDrawerAutoCollapse`, `CloseProgressDrawerButton_Click`, and `UpdateWorkflowToolStrip` all operate on the same workflow drawer controls and state fields.
  - `FormatCompletionReasonForDisplay` is only used by event-stream completion handling and naturally fits beside the workflow progress UI helpers.
  - The cluster can be extracted without changing behavior because it relies only on existing MainWindow controls and fields.

## Notes

- Keep scope narrow: move only the workflow-progress UI helpers and the completion-reason formatter.
- Do not mix this slice with prompt submission, voice capture, or transcript rendering logic.

## Phase 2

- Added `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.WorkflowProgress.cs`.
- Moved the workflow-progress cluster out of `MainWindow.axaml.cs`:
  - `ResetWorkflowProgressUi`
  - `ShowProgressDrawer`
  - `HideProgressDrawer`
  - `AutoCollapseProgressDrawerAsync`
  - `CancelProgressDrawerAutoCollapse`
  - `CloseProgressDrawerButton_Click`
  - `UpdateWorkflowToolStrip`
  - `FormatCompletionReasonForDisplay`
- Left unrelated helpers such as `ResolveThemeBrush` and the transcript/chat flow in `MainWindow.axaml.cs`.

## Phase 3

- Build command:
  - `dotnet build SirThaddeus.sln --no-restore -c Release`
- Result:
  - Passed with 0 errors.

- Test command:
  - `Remove-Item Env:ST_AUDIT_PATH -ErrorAction SilentlyContinue; Remove-Item Env:ST_SETTINGS_PATH -ErrorAction SilentlyContinue; dotnet test SirThaddeus.sln -c Release --no-build`
- Result:
  - 2101 passed, 0 failed, 0 skipped.

## Phase 4

- Confidence: 100%
- Reason: the progress drawer/tool-strip subsystem was extracted without widening scope, the build passed on the first attempt, and the full solution test suite passed without regressions.