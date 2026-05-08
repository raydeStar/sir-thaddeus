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
  voiceAvailable: boolean;
  voice: VoiceRuntimeStatus;
  pid: number;
  buildVersion: string;
}
