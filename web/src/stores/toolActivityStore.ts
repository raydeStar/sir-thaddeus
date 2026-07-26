import { create } from 'zustand';
import { subscribeWsEvents } from '../lib/wsEvents';
import { getTurnTrace } from '../lib/activityApi';
import type {
  ChatEffectCompleted,
  ChatEffectDescriptor,
  ChatEffectOutcome,
  ChatEffectProposed,
} from '@thaddeus/shared-types';

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
  effect?: ChatEffectDescriptor;
  effectOutcome?: ChatEffectOutcome;
}

interface ToolActivityStoreState {
  // Map of messageId → ordered list of activities for that turn.
  byMessage: Record<string, ToolActivity[]>;
  start: () => void;
  // Returns the activities for a specific assistant turn in order.
  forMessage: (messageId: string) => ToolActivity[];
  hydrateFromTraces: (messageIds: string[]) => Promise<void>;
}

let unsubscribe: (() => void) | null = null;
let started = false;
const pendingEffects = new Map<string, ChatEffectDescriptor>();

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

  hydrateFromTraces: async (messageIds) => {
    const missing = Array.from(new Set(messageIds))
      .filter((messageId) => messageId && !get().byMessage[messageId]);
    if (missing.length === 0) return;

    const hydrated: Record<string, ToolActivity[]> = {};
    await Promise.all(missing.map(async (messageId) => {
      try {
        const trace = await getTurnTrace(messageId);
        const byId = new Map<string, ToolActivity>();
        const proposedEffects = new Map<string, ChatEffectDescriptor>();
        const effectOutcomes = new Map<string, ChatEffectOutcome>();
        for (const event of trace.events) {
          if (event?.type === 'chat.effect.proposed' && event.payload) {
            const payload = event.payload as ChatEffectProposed;
            proposedEffects.set(payload.activityId, payload.effect);
            const existing = byId.get(payload.activityId);
            if (existing) byId.set(payload.activityId, { ...existing, effect: payload.effect });
          } else if (event?.type === 'chat.effect.completed' && event.payload) {
            const payload = event.payload as ChatEffectCompleted;
            proposedEffects.set(payload.activityId, payload.effect);
            effectOutcomes.set(payload.activityId, payload.outcome);
            const existing = byId.get(payload.activityId);
            if (existing) {
              byId.set(payload.activityId, {
                ...existing,
                effect: payload.effect,
                effectOutcome: payload.outcome,
              });
            }
          } else if (event?.type === 'chat.tool.started' && event.payload) {
            const payload = event.payload as ToolStartedPayload;
            byId.set(payload.activityId, {
              activityId: payload.activityId,
              threadId: payload.threadId,
              messageId: payload.messageId,
              tool: payload.tool,
              group: payload.group,
              argsPreview: payload.argsPreview,
              startedAt: payload.startedAt,
              status: 'running',
              effect: proposedEffects.get(payload.activityId),
              effectOutcome: effectOutcomes.get(payload.activityId),
            });
          } else if (event?.type === 'chat.tool.completed' && event.payload) {
            const payload = event.payload as ToolCompletedPayload;
            const existing = byId.get(payload.activityId);
            if (existing) {
              byId.set(payload.activityId, {
                ...existing,
                status: payload.ok ? 'ok' : 'error',
                durationMs: payload.durationMs,
                resultSnippet: payload.resultSnippet,
                error: payload.error,
              });
            }
          }
        }
        if (byId.size > 0) hydrated[messageId] = Array.from(byId.values());
      } catch {
        // Older turns may not have durable traces.
      }
    }));

    if (Object.keys(hydrated).length > 0) {
      set((state) => ({ byMessage: { ...state.byMessage, ...hydrated } }));
    }
  },

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
            effect: pendingEffects.get(p.activityId),
          };
          pendingEffects.delete(p.activityId);
          return {
            byMessage: { ...s.byMessage, [p.messageId]: [...existing, next] },
          };
        });
      } else if (evt.type === 'chat.effect.proposed' && evt.payload) {
        const p = evt.payload as ChatEffectProposed;
        pendingEffects.set(p.activityId, p.effect);
        set((s) => {
          const list = s.byMessage[p.messageId];
          if (!list) return s;
          return {
            byMessage: {
              ...s.byMessage,
              [p.messageId]: list.map((activity) =>
                activity.activityId === p.activityId
                  ? { ...activity, effect: p.effect }
                  : activity),
            },
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
      } else if (evt.type === 'chat.effect.completed' && evt.payload) {
        const p = evt.payload as ChatEffectCompleted;
        set((s) => {
          const list = s.byMessage[p.messageId];
          if (!list) return s;
          return {
            byMessage: {
              ...s.byMessage,
              [p.messageId]: list.map((activity) =>
                activity.activityId === p.activityId
                  ? { ...activity, effect: p.effect, effectOutcome: p.outcome }
                  : activity),
            },
          };
        });
      }
    });
  },
}));

export function teardownToolActivityStore() {
  if (unsubscribe) unsubscribe();
  unsubscribe = null;
  started = false;
  pendingEffects.clear();
}
