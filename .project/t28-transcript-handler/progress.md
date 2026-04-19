# Extract Transcript Helper Cluster From MainWindow

- Status: Done
- Branch: `task/extract-transcript-handler`
- Started: 2026-04-01
- Objective: Move transcript projection and adjacent UI helpers from `MainWindow.axaml.cs` into a dedicated partial so the main code-behind stops owning shared chat rendering behavior.
- Selection Basis: Next natural slice after `t27-submit-handler`, based on the remaining `MainWindow.axaml.cs` responsibilities and the fact that `AppendTranscript`, `ScrollChatToBottom`, and `ResolveThemeBrush` form one shared UI helper cluster used across the chat surfaces.

## Phase 1

- Selected as the next slice because it removes the last shared transcript projection path from the monolith without mixing in the assistant text parsing helpers.
- Evidence:
  - `AppendTranscript` is the shared message projection path used by submission, event streaming, settings, shutdown, diagnostics, and voice flows.
  - `ScrollChatToBottom` is coupled to transcript appends and event-stream updates.
  - `ResolveThemeBrush` is a small shared UI helper used across runtime status, action drawer, and voice/chat visuals.

## Notes

- Keep scope narrow: move only `AppendTranscript`, `ScrollChatToBottom`, and `ResolveThemeBrush`.
- Leave constructor/shutdown and the assistant text parsing/cleanup helpers in `MainWindow.axaml.cs` for the next slice.

## Phase 2

- Added `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.Transcript.cs`.
- Moved the transcript/UI helper cluster out of `MainWindow.axaml.cs`:
  - `AppendTranscript`
  - `ScrollChatToBottom`
  - `ResolveThemeBrush`

## Phase 3

- Build command:
  - `dotnet build SirThaddeus.sln --no-restore -c Release`
- Result:
  - First attempt failed with 1 error because `MainWindow.Transcript.cs` was missing `using Avalonia.Controls;` for `FindResource`.
  - Second attempt passed with 0 errors after adding the missing using.

- Test command:
  - `Remove-Item Env:ST_AUDIT_PATH -ErrorAction SilentlyContinue; Remove-Item Env:ST_SETTINGS_PATH -ErrorAction SilentlyContinue; dotnet test SirThaddeus.sln -c Release --no-build`
- Result:
  - 2101 passed, 0 failed, 0 skipped.

## Phase 4

- Confidence: 100%
- Reason: the slice stayed narrow, the only issue was a missing import in the new partial, and the final Release build plus full solution tests passed without regressions.
