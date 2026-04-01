# Extract Push To Talk Handler From MainWindow

- Status: Done
- Branch: `task/extract-push-to-talk-handler`
- Started: 2026-04-01
- Objective: Move the remaining push-to-talk button handlers, voice-input lifecycle methods, voice status helpers, and hotkey parsing helpers from `MainWindow.axaml.cs` into `MainWindow.PushToTalk.cs` so the voice-input subsystem lives in one partial.
- Selection Basis: Next narrow verifiable slice after `t24-workflow-progress-handler`, based on the remaining `MainWindow.axaml.cs` responsibility clusters and the fact that the leftover PTT methods are already owned conceptually by the existing push-to-talk partial.

## Phase 1

- Selected as the next low-risk seam because it expands an existing subsystem instead of introducing a new partial file.
- Evidence:
  - The remaining PTT button handlers and voice-input lifecycle methods already depend on helpers in `MainWindow.PushToTalk.cs`.
  - The voice status methods and hotkey parsing helpers are used to support push-to-talk and voice cancellation behavior.
  - The extraction avoids the higher-risk core submit/runtime connection path while still reducing the monolithic code-behind.

## Notes

- Keep scope narrow: move only the remaining push-to-talk/voice-input cluster.
- Do not mix this slice with the general window lifecycle or prompt submission flow.

## Phase 2

- Expanded `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.PushToTalk.cs`.
- Moved the remaining push-to-talk cluster out of `MainWindow.axaml.cs`:
  - `PttHoldButton_PointerPressed`
  - `PttHoldButton_PointerReleased`
  - `PttHoldButton_PointerCaptureLost`
  - `BeginPushToTalkAsync`
  - `EndPushToTalkAsync`
  - `SetVoiceChatStatus`
  - `IsVoiceResponseActive`
  - `IsConfiguredHotkeyDown`
  - `IsConfiguredHotkeyTriggerKey`
  - `ModifiersMatch`
  - `TryParseUiChord`
  - `TryParseUiKey`
  - `PttStateClasses`
- Left general chat helpers such as `AppendTranscript`, `ScrollChatToBottom`, and `ResolveThemeBrush` in `MainWindow.axaml.cs`.
- One compile fix was required after extraction: add `using FluentIcons.Avalonia;` and `using FluentIcons.Common;` for `Symbol` and `SymbolIcon` in `MainWindow.PushToTalk.cs`.

## Phase 3

- Build command:
  - `dotnet build SirThaddeus.sln --no-restore -c Release`
- Result:
  - First attempt failed with 7 errors: missing FluentIcons namespaces for `Symbol` and `SymbolIcon` in `MainWindow.PushToTalk.cs`.
  - Second attempt passed with 0 errors.

- Test command:
  - `Remove-Item Env:ST_AUDIT_PATH -ErrorAction SilentlyContinue; Remove-Item Env:ST_SETTINGS_PATH -ErrorAction SilentlyContinue; dotnet test SirThaddeus.sln -c Release --no-build`
- Result:
  - 2101 passed, 0 failed, 0 skipped.

## Phase 4

- Confidence: 100%
- Reason: the remaining voice-input seam now lives in the existing push-to-talk partial, the Release build passes, and the full solution test suite passed without regressions.