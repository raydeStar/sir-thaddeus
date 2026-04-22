// AUTO-GENERATED-CANDIDATE — hand-mirrored from packages/shared-types/cs/ActivityEntry.cs

export type ActivityKind = "ChatTurn" | "VoiceTurn" | "Automation" | "System";

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

export interface DiagnosticsResponse {
  uptimeSeconds: number;
  state: string;
  threadCount: number;
  threadStoreRoot: string;
  voiceAvailable: boolean;
  pid: number;
  buildVersion: string;
}
