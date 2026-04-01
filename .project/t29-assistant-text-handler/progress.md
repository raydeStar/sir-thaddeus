# Extract Assistant Text Helpers From MainWindow

- Status: Done
- Branch: `task/extract-assistant-text-handler`
- Started: 2026-04-01
- Objective: Move the regex-heavy assistant text parsing and cleanup helpers from `MainWindow.axaml.cs` into a dedicated partial so the primary code-behind no longer owns response-formatting internals.
- Selection Basis: Next natural slice after `t28-transcript-handler`, based on the remaining `MainWindow.axaml.cs` responsibilities and the fact that the assistant text regexes and parsing helpers are one cohesive, static subsystem.

## Phase 1

- Selected as the next slice because it removes the last large helper block from the monolith without mixing in window lifecycle setup and teardown.
- Evidence:
  - `ParseAssistantDisplayParts` and its helper methods are only part of assistant output normalization.
  - The regex fields and markdown cleanup helpers are only used inside that subsystem.
  - The event stream uses this block through a narrow surface: `ParseAssistantDisplayParts`.

## Notes

- Keep scope narrow: move the assistant text regex state, parsing helpers, and markdown cleanup helpers only.
- Leave constructor and `OnClosed` in `MainWindow.axaml.cs` unless the remaining lifecycle anchor still looks worth moving after this slice.

## Phase 2

- Added `apps/ui-avalonia/SirThaddeus.UI.Avalonia/MainWindow.AssistantText.cs`.
- Moved the assistant text helper cluster out of `MainWindow.axaml.cs`:
  - markdown regex fields
  - thinking/reasoning regex fields
  - `AssistantDisplayParts`
  - `ParseAssistantDisplayParts`
  - `CleanLlmOutput`
  - `IsLikelyInternalMarkerLine`
  - `TryExtractTaggedThinking`
  - `TryExtractStructuredThinkingPreamble`
  - `StripMarkdownFormatting`
  - `LooksLikeThinkingLead`
  - `IsReasoningLine`

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
- Reason: the extraction stayed fully within the assistant output formatting subsystem, and the final Release build plus full solution tests passed without regressions.
