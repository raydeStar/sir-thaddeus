import { create } from 'zustand';
import type { ActivityEntry, RuntimeEvent } from '@thaddeus/shared-types';
import { buildRuntimeWebSocketUrl, readRuntimeMetadata } from '../lib/runtime';
import { listActivity } from '../lib/activityApi';

/**
 * Activity log mirror. On connect we hydrate from the REST list endpoint and then
 * keep ourselves in sync by listening to activity.appended / activity.updated
 * frames on /ws. Entries are stored newest-first to match the wire order.
 */
interface ActivityStoreState {
  entries: ActivityEntry[];
  loading: boolean;
  error: string | null;
  connected: boolean;
  connect: () => Promise<void>;
  disconnect: () => void;
  refresh: () => Promise<void>;
}

let socket: WebSocket | null = null;

export const useActivityStore = create<ActivityStoreState>((set, get) => ({
  entries: [],
  loading: false,
  error: null,
  connected: false,

  connect: async () => {
    if (socket) {
      // Already streaming; just refresh the snapshot.
      await get().refresh();
      return;
    }
    await get().refresh();

    const { token } = readRuntimeMetadata();
    const url = buildRuntimeWebSocketUrl(token);
    if (!url) return;
    try {
      socket = new WebSocket(url);
    } catch (e) {
      set({ error: (e as Error).message });
      return;
    }
    socket.addEventListener('open', () => set({ connected: true }));
    socket.addEventListener('close', () => set({ connected: false }));
    socket.addEventListener('error', () => set({ error: 'websocket_error' }));
    socket.addEventListener('message', (msg) => {
      try {
        const evt = JSON.parse(msg.data as string) as RuntimeEvent<ActivityEntry>;
        if (!evt.payload) return;
        if (evt.type === 'activity.appended') {
          set((s) => ({
            entries: [evt.payload as ActivityEntry, ...s.entries.filter((e) => e.id !== (evt.payload as ActivityEntry).id)],
          }));
        } else if (evt.type === 'activity.updated') {
          set((s) => ({
            entries: s.entries.map((e) => (e.id === (evt.payload as ActivityEntry).id ? (evt.payload as ActivityEntry) : e)),
          }));
        }
      } catch {
        // ignore malformed frames
      }
    });
  },

  disconnect: () => {
    if (!socket) return;
    socket.close();
    socket = null;
    set({ connected: false });
  },

  refresh: async () => {
    set({ loading: true, error: null });
    try {
      const entries = await listActivity(100);
      set({ entries, loading: false });
    } catch (e) {
      set({ error: (e as Error).message, loading: false });
    }
  },
}));
