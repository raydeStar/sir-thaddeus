# Extract Window Lifecycle Anchor From MainWindow

- Status: Done
- Branch: `task/extract-window-lifecycle-handler`
- Started: 2026-04-01
- Objective: Move the `MainWindow` constructor and shutdown override from `MainWindow.axaml.cs` into the shell partial so the primary code-behind becomes a stable state-and-fields anchor only.
- Selection Basis: Final natural slice after `t29-assistant-text-handler`, based on the fact that `MainWindow.axaml.cs` now contains only the constructor and `OnClosed`, both of which are shell lifecycle behavior.

## Phase 1

- Selected as the final slice because the remaining methods are purely lifecycle glue and belong with the shell/opened/view-routing behavior already living in `MainWindow.Shell.cs`.
- Evidence:
  - `OnOpened`, constructor wiring, and `OnClosed` are one window lifecycle subsystem.
  - `MainWindow.axaml.cs` is now only 216 lines and contains no feature logic outside those methods.
  - Moving these methods leaves the primary partial as a clean state anchor for the other extracted subsystems.

## Notes

- Keep scope narrow: move only the constructor and `OnClosed` into `MainWindow.Shell.cs`.
- Leave field declarations and control accessors in `MainWindow.axaml.cs`.

## Phase 2

- Expanded `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.Shell.cs`.
- Moved the remaining lifecycle methods out of `MainWindow.axaml.cs`:
  - `MainWindow()`
  - `OnClosed(EventArgs e)`

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
- Reason: the slice only relocated lifecycle glue into the existing shell partial, and the final Release build plus full solution tests passed without regressions.
