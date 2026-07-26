// AUTO-GENERATED-CANDIDATE — hand-mirrored from packages/shared-types/cs/ChatThread.cs

export type ChatRole = "user" | "assistant" | "system";

export interface ChatMessage {
  id: string;
  role: ChatRole;
  text: string;
  createdAt: string;
  /**
   * Optional structured sources the assistant cited for this turn —
   * rendered as rich preview cards in the chat UI (thumbnails, favicons,
   * titles, domains, excerpts). Populated when a citation-producing tool
   * (currently web_search) fired; null otherwise.
   */
  sources?: ChatMessageSource[] | null;
}

export interface ChatMessageSource {
  /** Canonical URL the card links to. */
  url: string;
  /** Human-readable title; falls back to the URL host. */
  title?: string | null;
  /** Lowercased host used for favicon + display. */
  domain?: string | null;
  /** Short preview text, ≤ ~250 chars. */
  excerpt?: string | null;
  /**
   * data-URL for the favicon when the extractor captured one; the UI
   * falls back to a generic icon when absent.
   */
  favicon?: string | null;
  /** Absolute URL of a representative image (og:image or inline). */
  thumbnail?: string | null;
  /** ISO-8601 publish timestamp for dated articles; null otherwise. */
  publishedAt?: string | null;
}

export interface ChatThread {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messages: ChatMessage[];
  pinned?: boolean;
}

export interface ThreadSummary {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messageCount: number;
  lastMessagePreview?: string | null;
  pinned?: boolean;
}

export interface ThreadListResponse {
  threads: ThreadSummary[];
}

export interface ChatTurnStart {
  threadId: string;
  messageId: string;
  startedAt: string;
}

export interface ChatTurnDelta {
  threadId: string;
  messageId: string;
  text: string;
}

export interface ChatTurnComplete {
  threadId: string;
  messageId: string;
  finalText: string;
  completedAt: string;
  cancelled: boolean;
  sources?: ChatMessageSource[] | null;
}

export interface ChatUserMessageAppended {
  threadId: string;
  messageId: string;
  text: string;
  createdAt: string;
}

export interface ChatFootmanDecision {
  threadId: string;
  messageId: string;
  nextState: string;
  confidence: number;
  abstain: boolean;
  reasonCode: string;
  toolsKept: number;
  toolsTotal: number;
  elapsedMs: number;
  decidedAt: string;
}

export interface ChatMemoryRecalled {
  threadId: string;
  messageId: string;
  factsCount: number;
  eventsCount: number;
  chunksCount: number;
  nuggetsCount: number;
  /** Truncated preview of the assembled memory pack — first ~200 chars. */
  preview: string;
  durationMs: number;
  recalledAt: string;
}

export interface ChatEffectDescriptor {
  kind: "read" | "create" | "update" | "delete" | "restore" | "write" | "execute" | string;
  mutating: boolean;
  reversible: boolean;
  boundary: "local" | "web" | string;
  summary: string;
  target?: string | null;
  undoStrategy?: string | null;
  capability: string;
}

export interface ChatEffectOutcome {
  status: "observed" | "applied" | "failed" | string;
  evidence: "tool-result" | "tool-error" | "versioned-wiki-state" | string;
  independentlyVerified: boolean;
  resolvedTarget?: string | null;
}

export interface ChatEffectProposed {
  activityId: string;
  threadId: string;
  messageId: string;
  tool: string;
  effect: ChatEffectDescriptor;
  proposedAt: string;
}

export interface ChatEffectCompleted {
  activityId: string;
  threadId: string;
  messageId: string;
  tool: string;
  effect: ChatEffectDescriptor;
  outcome: ChatEffectOutcome;
  completedAt: string;
}

export type TurnRunState =
  | "awaitingapproval"
  | "awaiting_approval"
  | "running"
  | "pausing"
  | "paused"
  | "takingover"
  | "taking_over"
  | "cancelling"
  | "cancelled"
  | "completed"
  | "failed";

export interface TurnRunSnapshot {
  runId: string;
  threadId: string;
  userMessageId: string;
  assistantMessageId?: string | null;
  state: TurnRunState;
  checkpoint?: string | null;
  startedAt: string;
  updatedAt: string;
  detail?: string | null;
  version: number;
  isTerminal?: boolean;
  plan?: WorkPlan | null;
}

export interface ChatRunStateChanged extends TurnRunSnapshot {}

export type WorkPlanCapability =
  | "context"
  | "research"
  | "compose"
  | "durableoutput"
  | "durable_output"
  | "verify"
  | "general";

export type WorkPlanRisk = "low" | "medium" | "high";
export type WorkPlanStepStatus = "pending" | "active" | "done" | "skipped" | "blocked";

export interface WorkPlanStep {
  stepId: string;
  label: string;
  capability: WorkPlanCapability;
  risk: WorkPlanRisk;
  requiresPermission: boolean;
  status: WorkPlanStepStatus;
}

export interface WorkPlan {
  planId: string;
  version: number;
  intent: string;
  steps: WorkPlanStep[];
  risk: WorkPlanRisk;
  riskSummary: string;
  createdAt: string;
  updatedAt: string;
}

export const ChatTurnEventTypes = {
  Start: "chat.turn.start",
  Delta: "chat.turn.delta",
  Complete: "chat.turn.complete",
  UserMessageAppended: "chat.user.message",
  FootmanDecision: "chat.footman.decision",
  MemoryRecalled: "chat.memory.recalled",
  RunStateChanged: "chat.run.state",
  EffectProposed: "chat.effect.proposed",
  EffectCompleted: "chat.effect.completed",
} as const;
