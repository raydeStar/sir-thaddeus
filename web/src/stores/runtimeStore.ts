import { create } from 'zustand';
import type { RuntimeState, RuntimeStateEvent } from '@thaddeus/shared-types';
import { subscribeWsEvents } from '../lib/wsEvents';
import {
  connectRuntimeSocket,
  disconnectRuntimeSocket,
  isRuntimeSocketConnected,
  subscribeRuntimeSocketStatus,
} from '../lib/runtimeSocket';

/**
 * Zustand store mirroring the runtime's authoritative state. The socket itself
 * is owned by `lib/runtimeSocket` (one connection, one reconnect policy, shared
 * by every feature store); this store just projects the `runtime.state` slice
 * and the connection flag the header badge renders.
 */
interface RuntimeStoreState {
  connected: boolean;
  state: RuntimeState;
  lastEvent: RuntimeStateEvent | null;
  lastError: string | null;
  connect: () => void;
  disconnect: () => void;
}

let unsubscribeBus: (() => void) | null = null;
let unsubscribeStatus: (() => void) | null = null;

export const useRuntimeStore = create<RuntimeStoreState>((set) => ({
  connected: false,
  state: 'Idle',
  lastEvent: null,
  lastError: null,

  connect: () => {
    unsubscribeBus ??= subscribeWsEvents((evt) => {
      if (evt.type !== 'runtime.state' || !evt.payload) return;
      const payload = evt.payload as RuntimeStateEvent;
      set({ state: payload.state, lastEvent: payload });
    });

    unsubscribeStatus ??= subscribeRuntimeSocketStatus((connected) => {
      set(connected
        ? { connected: true, lastError: null }
        // Keep the last error text out of the way while a reconnect is pending;
        // the badge already communicates "not connected" from the flag.
        : { connected: false });
    });

    connectRuntimeSocket();
    set({ connected: isRuntimeSocketConnected() });
  },

  disconnect: () => {
    unsubscribeBus?.();
    unsubscribeBus = null;
    unsubscribeStatus?.();
    unsubscribeStatus = null;
    disconnectRuntimeSocket();
    set({ connected: false });
  },
}));
