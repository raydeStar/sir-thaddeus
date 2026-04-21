import { create } from 'zustand';
import {
  listPendingPermissions,
  respondToPermission,
  type PendingPermission,
  type PermissionResponse,
} from '../lib/permissionsApi';
import { subscribeWsEvents } from '../lib/wsEvents';

/**
 * Tracks outstanding tool-permission prompts from the runtime. The modal
 * reads the head of the queue and resolves it via the REST endpoint. New
 * prompts arrive as `permission.request` WS events; the runtime emits
 * `permission.resolved` when any pending prompt is answered (by us or by
 * another client), so we can drop it from the queue.
 */
interface PermissionsStoreState {
  queue: PendingPermission[];
  start: () => void;
  resolve: (id: string, decision: PermissionResponse) => Promise<void>;
  dismiss: (id: string) => void;
}

let unsubscribe: (() => void) | null = null;
let started = false;

function enqueueUnique(queue: PendingPermission[], req: PendingPermission): PendingPermission[] {
  if (queue.some((q) => q.id === req.id)) return queue;
  return [...queue, req];
}

export const usePermissionsStore = create<PermissionsStoreState>((set, get) => ({
  queue: [],

  start: () => {
    if (started) return;
    started = true;

    // Backfill: server may have prompts outstanding from before we connected
    // (shouldn't happen often, but e.g. after a page refresh mid-prompt).
    void (async () => {
      try {
        const pending = await listPendingPermissions();
        if (pending.length > 0) {
          set((s) => ({ queue: pending.reduce(enqueueUnique, s.queue) }));
        }
      } catch {
        /* non-fatal: if the endpoint isn't up yet the WS stream will feed us */
      }
    })();

    unsubscribe = subscribeWsEvents((evt) => {
      if (evt.type === 'permission.request' && evt.payload) {
        const req = evt.payload as PendingPermission;
        set((s) => ({ queue: enqueueUnique(s.queue, req) }));
      } else if (evt.type === 'permission.resolved' && evt.payload) {
        const resolved = evt.payload as { id?: string };
        if (resolved.id) {
          set((s) => ({ queue: s.queue.filter((q) => q.id !== resolved.id) }));
        }
      }
    });
  },

  resolve: async (id, decision) => {
    // Optimistically drop from the queue so the modal closes immediately.
    // If the POST fails we re-queue from whatever the server reports.
    const prior = get().queue;
    set({ queue: prior.filter((q) => q.id !== id) });
    try {
      await respondToPermission(id, decision);
    } catch (err) {
      // Roll back — let the user try again.
      console.warn('[permissions] respond failed', err);
      const current = get().queue;
      const original = prior.find((q) => q.id === id);
      if (original && !current.some((q) => q.id === id)) {
        set({ queue: [original, ...current] });
      }
    }
  },

  dismiss: (id) => {
    set((s) => ({ queue: s.queue.filter((q) => q.id !== id) }));
  },
}));

// Auto-teardown for HMR / tests.
export function teardownPermissionsStore() {
  if (unsubscribe) unsubscribe();
  unsubscribe = null;
  started = false;
}
