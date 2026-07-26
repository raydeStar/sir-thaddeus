import { create } from 'zustand';
import {
  listPendingPermissions,
  listSessionGrants,
  respondToPermission,
  type PendingPermission,
  type PermissionResponse,
  type PermissionScope,
} from '../lib/permissionsApi';
import { subscribeWsEvents } from '../lib/wsEvents';

export interface ResolvedPermission {
  request: PendingPermission;
  decision: PermissionResponse;
  scope: PermissionScope;
  resolvedAt: string;
}

/**
 * Tracks outstanding tool-permission prompts from the runtime. The modal
 * reads the head of the queue and resolves it via the REST endpoint. New
 * prompts arrive as `permission.request` WS events; the runtime emits
 * `permission.resolved` when any pending prompt is answered (by us or by
 * another client), so we can drop it from the queue.
 */
interface PermissionsStoreState {
  queue: PendingPermission[];
  resolved: ResolvedPermission[];
  /** Scope strings for grants the runtime gate is currently honouring. */
  sessionGrants: string[];
  error: string | null;
  start: () => void;
  resolve: (id: string, decision: PermissionResponse, scope?: PermissionScope) => Promise<void>;
  refreshSessionGrants: () => Promise<void>;
  dismiss: (id: string) => void;
}

let unsubscribe: (() => void) | null = null;
let started = false;

/** Older runtimes omit `scope`; preserve known scopes and default to group. */
function withScopeDefault(req: PendingPermission): PendingPermission {
  const scope = req.scope === 'tool' || req.scope === 'call' ? req.scope : 'group';
  return { ...req, scope };
}

function enqueueUnique(queue: PendingPermission[], req: PendingPermission): PendingPermission[] {
  if (queue.some((q) => q.id === req.id)) return queue;
  return [...queue, withScopeDefault(req)];
}

export const usePermissionsStore = create<PermissionsStoreState>((set, get) => ({
  queue: [],
  resolved: [],
  sessionGrants: [],
  error: null,

  refreshSessionGrants: async () => {
    try {
      set({ sessionGrants: await listSessionGrants() });
    } catch {
      // Posture is ambient, not load-bearing; the connection badge already
      // reports an unreachable runtime.
    }
  },

  start: () => {
    if (started) return;
    started = true;

    void get().refreshSessionGrants();

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
        // A decision anywhere (this tab or another client) can widen or clear
        // session scope, so re-read rather than guess.
        void get().refreshSessionGrants();
      }
    });
  },

  resolve: async (id, decision, scope) => {
    const prior = get().queue;
    const original = prior.find((q) => q.id === id);
    const resolvedScope: PermissionScope =
      original?.scope === 'call'
        ? 'call'
        : scope === 'tool' || scope === 'call'
          ? scope
          : 'group';
    set({ queue: prior.filter((q) => q.id !== id), error: null });
    try {
      await respondToPermission(id, decision, resolvedScope);
      if (original) {
        set((s) => ({
          resolved: [
            ...s.resolved.slice(-49),
            {
              request: original,
              decision,
              scope: resolvedScope,
              resolvedAt: new Date().toISOString(),
            },
          ],
        }));
      }
    } catch (err) {
      // Roll back — let the user try again.
      console.warn('[permissions] respond failed', err);
      const current = get().queue;
      if (original && !current.some((q) => q.id === id)) {
        set({
          queue: [original, ...current],
          error: (err as Error).message || 'Could not record the permission decision.',
        });
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
