// AUTO-GENERATED-CANDIDATE — hand-mirrored from packages/shared-schemas/runtime-state-event.schema.json.
import type { RuntimeState } from "./runtime-state";

export interface RuntimeStateEvent {
  state: RuntimeState;
  turnId?: string;
  threadId?: string;
  activeToolCall?: {
    toolId: string;
    /** ISO-8601 */
    startedAt: string;
    humanSummary: string;
  };
  pendingPermission?: {
    requestId: string;
    capability: string;
    humanSummary: string;
  };
  ttsQueueDepth?: number;
  inputSource?: "voice" | "text";
  lastError?: {
    code: string;
    humanSummary: string;
    correlationId: string;
  };
  /** ISO-8601 */
  timestamp: string;
}
