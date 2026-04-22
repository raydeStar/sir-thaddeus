/**
 * Tiny browser-side fan-out for WebSocket events produced by the runtime.
 *
 * The runtime store owns the socket, but multiple feature stores
 * (permissions, tool activity, future slices) want to see the same events.
 * Rather than coupling those stores to the runtime store's internals we
 * publish every decoded frame here, and each feature store subscribes to
 * the event types it cares about.
 */

export interface RuntimeWsEvent {
  type: string;
  id?: string;
  timestamp?: string;
  correlationId?: string | null;
  payload?: unknown;
}

type Listener = (evt: RuntimeWsEvent) => void;

const listeners = new Set<Listener>();

export function publishWsEvent(evt: RuntimeWsEvent): void {
  for (const listener of listeners) {
    try {
      listener(evt);
    } catch {
      /* don't let one broken consumer kill delivery to others */
    }
  }
}

export function subscribeWsEvents(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}
