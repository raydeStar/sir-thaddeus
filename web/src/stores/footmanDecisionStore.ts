import { create } from 'zustand';
import { subscribeWsEvents } from '../lib/wsEvents';

export interface FootmanDecision {
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

interface FootmanDecisionStoreState {
  byMessage: Record<string, FootmanDecision>;
  start: () => void;
  forMessage: (messageId: string) => FootmanDecision | undefined;
}

let unsubscribe: (() => void) | null = null;
let started = false;

export const useFootmanDecisionStore = create<FootmanDecisionStoreState>((set, get) => ({
  byMessage: {},

  forMessage: (messageId) => get().byMessage[messageId],

  start: () => {
    if (started) return;
    started = true;

    unsubscribe = subscribeWsEvents((evt) => {
      if (evt.type !== 'chat.footman.decision' || !evt.payload) return;
      const p = evt.payload as FootmanDecision;
      set((s) => ({
        byMessage: { ...s.byMessage, [p.messageId]: p },
      }));
    });
  },
}));

export function teardownFootmanDecisionStore() {
  if (unsubscribe) unsubscribe();
  unsubscribe = null;
  started = false;
}
