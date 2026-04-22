import { create } from 'zustand';
import type { AutomationProposal } from '@thaddeus/shared-types';
import { subscribeWsEvents } from '../lib/wsEvents';

/**
 * UI state for a single pending / resolved automation proposal. One
 * entry per assistant message that called <code>propose_automation</code>.
 *
 * Phase C — the runtime emits a <code>chat.automation.proposed</code>
 * event when the model calls the virtual tool; the chat thread renders
 * an editable confirmation card backed by this store. Once the user
 * clicks Create or Cancel the status flips to <code>created</code> /
 * <code>cancelled</code> so re-renders don't re-open the editor.
 */
export type ProposalStatus = 'pending' | 'creating' | 'created' | 'cancelled' | 'error';

export interface ProposalState {
  proposal: AutomationProposal;
  status: ProposalStatus;
  automationId?: string;
  error?: string;
}

interface ProposalsStoreState {
  byMessage: Record<string, ProposalState>;
  start: () => void;
  setStatus: (
    messageId: string,
    patch: Partial<Omit<ProposalState, 'proposal'>>,
  ) => void;
}

let started = false;

export const useProposalsStore = create<ProposalsStoreState>((set) => ({
  byMessage: {},

  setStatus: (messageId, patch) =>
    set((s) => {
      const existing = s.byMessage[messageId];
      if (!existing) return s;
      return {
        byMessage: {
          ...s.byMessage,
          [messageId]: { ...existing, ...patch },
        },
      };
    }),

  start: () => {
    if (started) return;
    started = true;

    subscribeWsEvents((evt) => {
      if (evt.type !== 'chat.automation.proposed' || !evt.payload) return;
      const p = evt.payload as AutomationProposal;
      set((s) => {
        // A second proposed event on the same message replaces the prior
        // proposal (e.g. the model corrected itself). We don't stack them.
        return {
          byMessage: {
            ...s.byMessage,
            [p.messageId]: { proposal: p, status: 'pending' },
          },
        };
      });
    });
  },
}));
