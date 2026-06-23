// AUTO-GENERATED-CANDIDATE — hand-mirrored from packages/shared-types/cs/ActivityEntry.cs

export type ActivityKind = "ChatTurn" | "VoiceTurn" | "Routine" | "System";

export type ActivityStatus = "Running" | "Ok" | "Cancelled" | "Failed";

export interface ActivityEntry {
  id: string;
  kind: ActivityKind;
  summary: string;
  status: ActivityStatus;
  startedAt: string;
  completedAt?: string | null;
  threadId?: string | null;
  detail?: string | null;
}

export interface ActivityListResponse {
  entries: ActivityEntry[];
}

export interface VoiceRuntimeStatus {
  voiceHostEnabled: boolean;
  hostReachable: boolean;
  asrReady: boolean;
  ttsReady: boolean;
  inputAvailable: boolean;
  outputAvailable: boolean;
  status: string;
  message: string;
  errorCode?: string | null;
  body?: string | null;
  elapsedMs: number;
}

export interface DiagnosticsResponse {
  uptimeSeconds: number;
  state: string;
  threadCount: number;
  threadStoreRoot: string;
  logsRoot: string;
  /** Directory containing per-turn JSONL trace files keyed by messageId. */
  turnsRoot: string;
  voiceAvailable: boolean;
  voice: VoiceRuntimeStatus;
  pid: number;
  buildVersion: string;
}

/** One entry in GET /api/turns. */
export interface TurnTraceSummary {
  messageId: string;
  threadId?: string | null;
  modifiedAt: string;
  sizeBytes: number;
  eventCount: number;
  lastEventType?: string | null;
}

/** Response for GET /api/turns. */
export interface TurnTraceListResponse {
  turns: TurnTraceSummary[];
}

/** Response for GET /api/turns/{messageId}/trace. Events are raw RuntimeEvent envelopes. */
export interface TurnTraceResponse {
  messageId: string;
  events: Array<Record<string, unknown> | null>;
}

/** One entry in GET /api/logs. */
export interface RuntimeLogSummary {
  fileName: string;
  modifiedAt: string;
  sizeBytes: number;
  lineCount: number;
  lastLine?: string | null;
}

/** Response for GET /api/logs. */
export interface RuntimeLogListResponse {
  logs: RuntimeLogSummary[];
}

export interface RuntimeLogLine {
  number: number;
  text: string;
}

/** Response for GET /api/logs/{fileName}. */
export interface RuntimeLogResponse {
  fileName: string;
  lines: RuntimeLogLine[];
}
