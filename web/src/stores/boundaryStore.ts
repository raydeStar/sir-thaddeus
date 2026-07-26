import { create } from 'zustand';
import { getSettings } from '../lib/settingsApi';

/**
 * Ambient local-vs-outbound posture for the shell header.
 *
 * The header badge must never assert a boundary it has not actually read. Until
 * settings load we report `unknown` and the badge stays neutral rather than
 * claiming "Local" on faith — a boundary indicator that is right by accident is
 * worse than one that admits it does not know yet.
 */
export type BoundaryPosture = 'unknown' | 'offline' | 'web-allowed';

interface BoundaryState {
  posture: BoundaryPosture;
  /** Re-read the persisted privacy settings. */
  refresh: () => Promise<void>;
  /** Optimistic update for in-app toggles, so the header reacts immediately. */
  setOfflineMode: (offline: boolean) => void;
}

export const useBoundaryStore = create<BoundaryState>((set) => ({
  posture: 'unknown',
  refresh: async () => {
    try {
      const doc = await getSettings();
      set({ posture: doc.privacy.offlineMode ? 'offline' : 'web-allowed' });
    } catch {
      // Leave the posture as-is; an unreachable runtime is already surfaced by
      // the connection badge and must not be reported as a boundary claim.
    }
  },
  setOfflineMode: (offline) => set({ posture: offline ? 'offline' : 'web-allowed' }),
}));
