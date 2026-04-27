import { create } from 'zustand';
import type { RuntimeState, RuntimeStateEvent, RuntimeEvent } from '@thaddeus/shared-types';
import { buildRuntimeWebSocketUrl, readRuntimeMetadata } from '../lib/runtime';
import { publishWsEvent } from '../lib/wsEvents';

/**
 * Zustand store mirroring the runtime's authoritative state, fed by the WebSocket
 * stream the runtime publishes on /ws. Phase 1 only consumes `runtime.state` events;
 * later phases will add tool-call, permission, and TTS events as separate slices.
 */
interface RuntimeStoreState {
  connected: boolean;
  state: RuntimeState;
  lastEvent: RuntimeStateEvent | null;
  lastError: string | null;
  connect: () => void;
  disconnect: () => void;
}

let socket: WebSocket | null = null;

export const useRuntimeStore = create<RuntimeStoreState>((set) => ({
  connected: false,
  state: 'Idle',
  lastEvent: null,
  lastError: null,

  connect: () => {
    if (socket) return;
    const { token } = readRuntimeMetadata();
    const url = buildRuntimeWebSocketUrl(token);
    if (!url) return;

    try {
      socket = new WebSocket(url);
    } catch (e) {
      set({ lastError: (e as Error).message });
      return;
    }

    socket.addEventListener('open', () => set({ connected: true, lastError: null }));
    socket.addEventListener('close', () => set({ connected: false }));
    socket.addEventListener('error', () =>
      set({ lastError: 'WebSocket connection failed.' }),
    );
    socket.addEventListener('message', (msg) => {
      try {
        const evt = JSON.parse(msg.data as string) as RuntimeEvent<RuntimeStateEvent>;
        // Runtime-state slice lives here; everything else goes on the bus.
        if (evt.type === 'runtime.state' && evt.payload) {
          set({ state: evt.payload.state, lastEvent: evt.payload });
        }
        publishWsEvent({
          type: evt.type,
          id: evt.id,
          timestamp: evt.timestamp,
          correlationId: evt.correlationId ?? null,
          payload: evt.payload,
        });
      } catch {
        // ignore malformed frames; the runtime is the authority
      }
    });
  },

  disconnect: () => {
    if (!socket) return;
    socket.close();
    socket = null;
    set({ connected: false });
  },
}));
