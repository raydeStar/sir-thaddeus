# Extract Submit Handler From MainWindow

- Status: Done
- Branch: `task/extract-submit-handler`
- Started: 2026-04-01
- Objective: Move the send-button handler and core prompt submission workflow from `MainWindow.axaml.cs` into a dedicated partial so the main code-behind keeps shrinking toward transcript and generic helper responsibilities.
- Selection Basis: Next natural slice after `t26-shell-view-handler`, based on the remaining `MainWindow.axaml.cs` responsibilities and the fact that `SendButton_Click` and `SubmitPromptAsync` form one cohesive submission subsystem.

## Phase 1

- Selected as the next slice because it isolates the primary prompt submission workflow without mixing in transcript projection or generic UI helpers.
- Evidence:
  - `SendButton_Click` is a thin entry point into `SubmitPromptAsync`.
  - `SubmitPromptAsync` encapsulates runtime connectivity, pending prompt fallback, attachment injection, conversation seeding, run creation, and event stream startup.
  - The methods are already referenced as one workflow from message-surface retry actions, push-to-talk auto-submit, and runtime reconnection logic.

## Notes

- Keep scope narrow: move only `SendButton_Click` and `SubmitPromptAsync`.
- Leave `AppendTranscript`, `ScrollChatToBottom`, `ResolveThemeBrush`, and the reasoning/cleanup helpers in `MainWindow.axaml.cs` for now.

## Phase 2

- Added `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.Submission.cs`.
- Moved the submission cluster out of `MainWindow.axaml.cs`:
  - `SendButton_Click`
  - `SubmitPromptAsync`
- Left transcript projection and generic helpers in `MainWindow.axaml.cs`.

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
- Reason: the submission seam was extracted narrowly, the build passed on the first attempt, and the full solution test suite passed without regressions.