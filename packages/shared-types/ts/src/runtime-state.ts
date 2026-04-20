// AUTO-GENERATED-CANDIDATE (currently hand-mirrored from packages/shared-schemas).
// Do not edit by hand once generation is wired up in Phase 1.5.
// Source of truth: packages/shared-schemas/runtime-state.schema.json

export const RuntimeStates = [
  "Idle",
  "Listening",
  "Transcribing",
  "Thinking",
  "AwaitingPermission",
  "ExecutingTools",
  "Speaking",
  "Paused",
  "Error",
  "Stopping",
] as const;

export type RuntimeState = (typeof RuntimeStates)[number];
