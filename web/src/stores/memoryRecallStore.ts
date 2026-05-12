import { create } from 'zustand';
import { subscribeWsEvents } from '../lib/wsEvents';
import { getTurnTrace } from '../lib/activityApi';
import type { ChatMemoryRecalled } from '@thaddeus/shared-types';

/**
 * Subscribes to chat.memory.recalled events over the runtime WebSocket
 * and indexes them by messageId so the MemoryRecallChip can render
 * above the corresponding assistant message. Mirrors the
 * footmanDecisionStore pattern.
 */
interface MemoryRecallStoreState {
  byMessage: Record<string, ChatMemoryRecalled>;
  start: () => void;
  forMessage: (messageId: string) => ChatMemoryRecalled | undefined;
  hydrateFromTraces: (messageIds: string[]) => Promise<void>;
}

let unsubscribe: (() => void) | null = null;
let started = false;

export const useMemoryRecallStore = create<MemoryRecallStoreState>((set, get) => ({
  byMessage: {},

  forMessage: (messageId) => get().byMessage[messageId],

  hydrateFromTraces: async (messageIds) => {
    const missing = Array.from(new Set(messageIds))
      .filter((id) => id && !get().byMessage[id]);
    if (missing.length === 0) return;

    const recalled: Record<string, ChatMemoryRecalled> = {};
    await Promise.all(missing.map(async (messageId) => {
      try {
        const trace = await getTurnTrace(messageId);
        const evt = trace.events.find((e) =>
          e?.type === 'chat.memory.recalled' && e.payload);
        const payload = evt?.payload as ChatMemoryRecalled | undefined;
        if (payload?.messageId) recalled[payload.messageId] = payload;
      } catch {
        // Older turns may not have trace files. Missing history should not
        // interrupt chat opening.
      }
    }));

    if (Object.keys(recalled).length === 0) return;
    set((s) => ({
      byMessage: { ...s.byMessage, ...recalled },
    }));
  },

  start: () => {
    if (started) return;
    started = true;

    unsubscribe = subscribeWsEvents((evt) => {
      if (evt.type !== 'chat.memory.recalled' || !evt.payload) return;
      const p = evt.payload as ChatMemoryRecalled;
      set((s) => ({
        byMessage: { ...s.byMessage, [p.messageId]: p },
      }));
    });
  },
}));

export function teardownMemoryRecallStore() {
  if (unsubscribe) unsubscribe();
  unsubscribe = null;
  started = false;
}
