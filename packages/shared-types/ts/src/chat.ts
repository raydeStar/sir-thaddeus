// AUTO-GENERATED-CANDIDATE — hand-mirrored from packages/shared-types/cs/ChatThread.cs

export type ChatRole = "user" | "assistant" | "system";

export interface ChatMessage {
  id: string;
  role: ChatRole;
  text: string;
  createdAt: string;
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
}

export const ChatTurnEventTypes = {
  Start: "chat.turn.start",
  Delta: "chat.turn.delta",
  Complete: "chat.turn.complete",
} as const;
