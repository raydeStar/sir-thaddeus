import { create } from 'zustand';
import { subscribeWsEvents } from '../lib/wsEvents';

/**
 * One tool invocation as it flows through the UI. Starts with status
 * <code>running</code>, transitions to <code>ok</code> / <code>error</code>
 * when the completed event lands. Indexed by <code>activityId</code> so
 * the completed event can find its starting record.
 */
export interface ToolActivity {
  activityId: string;
  threadId: string;
  messageId: string;
  tool: string;
  group: string;
  argsPreview: string;
  startedAt: string;
  status: 'running' | 'ok' | 'error';
  durationMs?: number;
  resultSnippet?: string | null;
  error?: string | null;
}

interface ToolActivityStoreState {
  // Map of messageId → ordered list of activities for that turn.
  byMessage: Record<string, ToolActivity[]>;
  start: () => void;
  // Returns the activities for a specific assistant turn in order.
  forMessage: (messageId: string) => ToolActivity[];
}

let unsubscribe: (() => void) | null = null;
let started = false;

interface ToolStartedPayload {
  activityId: string;
  threadId: string;
  messageId: string;
  tool: string;
  group: string;
  argsPreview: string;
  startedAt: string;
}

interface ToolCompletedPayload {
  activityId: string;
  threadId: string;
  messageId: string;
  tool: string;
  ok: boolean;
  durationMs: number;
  resultSnippet: string | null;
  error: string | null;
  completedAt: string;
}

export const useToolActivityStore = create<ToolActivityStoreState>((set, get) => ({
  byMessage: {},

  forMessage: (messageId) => get().byMessage[messageId] ?? [],

  start: () => {
    if (started) return;
    started = true;

    unsubscribe = subscribeWsEvents((evt) => {
      if (evt.type === 'chat.tool.started' && evt.payload) {
        const p = evt.payload as ToolStartedPayload;
        set((s) => {
          const existing = s.byMessage[p.messageId] ?? [];
          // Dedupe in case the socket replays.
          if (existing.some((a) => a.activityId === p.activityId)) return s;
          const next: ToolActivity = {
            activityId: p.activityId,
            threadId: p.threadId,
            messageId: p.messageId,
            tool: p.tool,
            group: p.group,
            argsPreview: p.argsPreview,
            startedAt: p.startedAt,
            status: 'running',
          };
          return {
            byMessage: { ...s.byMessage, [p.messageId]: [...existing, next] },
          };
        });
      } else if (evt.type === 'chat.tool.completed' && evt.payload) {
        const p = evt.payload as ToolCompletedPayload;
        set((s) => {
          const list = s.byMessage[p.messageId];
          if (!list) return s;
          const updated = list.map((a) =>
            a.activityId === p.activityId
              ? {
                  ...a,
                  status: p.ok ? 'ok' : 'error',
                  durationMs: p.durationMs,
                  resultSnippet: p.resultSnippet,
                  error: p.error,
                } as ToolActivity
              : a,
          );
          return { byMessage: { ...s.byMessage, [p.messageId]: updated } };
        });
      }
    });
  },
}));

export function teardownToolActivityStore() {
  if (unsubscribe) unsubscribe();
  unsubscribe = null;
  started = false;
}
