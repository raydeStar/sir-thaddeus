# Extract Shell View Handler From MainWindow

- Status: Done
- Branch: `task/extract-shell-view-handler`
- Started: 2026-04-01
- Objective: Move the window shell startup, tab switching, and window-scoped keyboard routing methods from `MainWindow.axaml.cs` into a dedicated partial so the remaining code-behind is reduced toward the core submit flow and shared helpers.
- Selection Basis: Next narrow verifiable slice after `t25-push-to-talk-handler`, based on the remaining `MainWindow.axaml.cs` responsibility clusters and the fact that the open lifecycle, view switching, and window keyboard routing all belong to one shell-level UI subsystem.

## Phase 1

- Selected as the next low-risk seam because it is cohesive and leaves the higher-risk prompt submission/runtime flow untouched.
- Evidence:
  - `OnOpened`, `ViewTab_Click`, and `SetActiveView` are all window shell and navigation responsibilities.
  - `Window_KeyDown` and `Window_KeyUp` route shell-level keyboard behavior including view shortcuts and window-scoped push-to-talk fallback.
  - `SetActiveView` is already shared across multiple partials, so moving it into a dedicated shell partial improves ownership without changing behavior.

## Notes

- Keep scope narrow: move only the shell and view-switching cluster.
- Do not mix this slice with `SendButton_Click`, `SubmitPromptAsync`, transcript projection, or generic UI helpers.

## Phase 2

- Added `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.Shell.cs`.
- Moved the shell and view-switching cluster out of `MainWindow.axaml.cs`:
  - `OnOpened`
  - `ViewTab_Click`
  - `SetActiveView`
  - `Window_KeyDown`
  - `Window_KeyUp`
- Left the remaining core flow and generic helpers in `MainWindow.axaml.cs`:
  - `SendButton_Click`
  - `SubmitPromptAsync`
  - `AppendTranscript`
  - `ScrollChatToBottom`
  - `ResolveThemeBrush`

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
- Reason: the shell/view seam was extracted without widening scope, the build passed on the first attempt, and the full solution test suite passed without regressions.