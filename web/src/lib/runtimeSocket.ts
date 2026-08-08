import type { RuntimeEvent } from '@thaddeus/shared-types';
import { buildRuntimeWebSocketUrl, readRuntimeMetadata } from './runtime';
import { publishWsEvent } from './wsEvents';

/**
 * Single owner of the runtime `/ws` connection.
 *
 * Every feature store reads runtime events off the {@link publishWsEvent} bus,
 * so the app needs exactly one socket and one reconnect policy. Two things this
 * module exists to guarantee:
 *
 * 1. **Connect-before-send.** The runtime starts an assistant turn on a
 *    background task the moment `POST /messages` is accepted, and the
 *    broadcaster has no replay buffer — a `chat.turn.start` published before the
 *    socket finishes its handshake is gone for good, which leaves the UI with no
 *    streaming text and no progress card for the whole turn. Callers await
 *    {@link ensureRuntimeSocket} before kicking off work that emits events.
 * 2. **Reconnect.** A dropped socket (runtime restart, machine sleep) used to
 *    strand the UI permanently: the old code nulled or kept a dead handle and
 *    never dialled again, so pills, permissions, and streaming all went quiet
 *    until a reload. Drops now retry with backoff and notify subscribers so they
 *    can re-sync the state they missed.
 */

type StatusListener = (connected: boolean) => void;
type ReconnectListener = () => void;

// Local runtime on loopback: retry fast at first, then back off so a runtime
// that is genuinely down doesn't spin. Capped well under a human's patience.
const RETRY_SCHEDULE_MS = [250, 500, 1_000, 2_000, 4_000];
const MAX_RETRY_MS = 5_000;
const DEFAULT_OPEN_TIMEOUT_MS = 2_000;

let socket: WebSocket | null = null;
let desired = false;
let retryAttempt = 0;
let retryTimer: number | null = null;
let openWaiters: ((connected: boolean) => void)[] = [];
let hasEverOpened = false;

const statusListeners = new Set<StatusListener>();
const reconnectListeners = new Set<ReconnectListener>();

/** True when a socket is present and fully open. */
export function isRuntimeSocketConnected(): boolean {
  return socket?.readyState === WebSocket.OPEN;
}

/**
 * Opens the runtime socket and keeps it open until
 * {@link disconnectRuntimeSocket} is called. Safe to call repeatedly.
 */
export function connectRuntimeSocket(): void {
  desired = true;
  openSocket();
}

/** Tears the socket down and stops reconnecting. */
export function disconnectRuntimeSocket(): void {
  desired = false;
  clearRetry();
  const current = socket;
  socket = null;
  if (current) {
    // Drop handlers first so the close doesn't schedule a reconnect.
    current.onopen = null;
    current.onclose = null;
    current.onerror = null;
    current.onmessage = null;
    try {
      current.close();
    } catch {
      /* already closing */
    }
  }
  settleOpenWaiters(false);
  notifyStatus(false);
}

/**
 * Resolves once the socket is open, or `false` if it can't connect within
 * `timeoutMs`. Never rejects — callers treat a false result as "proceed
 * anyway, events may be missed" rather than a hard failure, because a turn
 * that runs without live events is still recoverable via re-sync.
 */
export function ensureRuntimeSocket(timeoutMs = DEFAULT_OPEN_TIMEOUT_MS): Promise<boolean> {
  connectRuntimeSocket();
  if (isRuntimeSocketConnected()) return Promise.resolve(true);
  if (!socket) return Promise.resolve(false); // no runtime metadata — nothing to wait for

  return new Promise<boolean>((resolve) => {
    let settled = false;
    const finish = (connected: boolean) => {
      if (settled) return;
      settled = true;
      window.clearTimeout(timer);
      resolve(connected);
    };
    const timer = window.setTimeout(() => finish(isRuntimeSocketConnected()), timeoutMs);
    openWaiters.push(finish);
  });
}

/** Subscribes to open/closed transitions. Returns an unsubscribe function. */
export function subscribeRuntimeSocketStatus(listener: StatusListener): () => void {
  statusListeners.add(listener);
  listener(isRuntimeSocketConnected());
  return () => {
    statusListeners.delete(listener);
  };
}

/**
 * Subscribes to *re*-connections only (not the first connect). Consumers use
 * this to re-fetch whatever they may have missed while the socket was down.
 */
export function subscribeRuntimeReconnect(listener: ReconnectListener): () => void {
  reconnectListeners.add(listener);
  return () => {
    reconnectListeners.delete(listener);
  };
}

function openSocket(): void {
  if (!desired) return;
  if (socket && socket.readyState !== WebSocket.CLOSED) return;

  const { token } = readRuntimeMetadata();
  const url = buildRuntimeWebSocketUrl(token);
  if (!url) return;

  let next: WebSocket;
  try {
    next = new WebSocket(url);
  } catch {
    scheduleRetry();
    return;
  }
  socket = next;

  next.addEventListener('open', () => {
    if (socket !== next) return;
    const reconnected = hasEverOpened;
    hasEverOpened = true;
    retryAttempt = 0;
    clearRetry();
    settleOpenWaiters(true);
    notifyStatus(true);
    if (reconnected) notifyReconnect();
  });

  next.addEventListener('message', (event) => {
    try {
      const decoded = JSON.parse(event.data as string) as RuntimeEvent<unknown>;
      publishWsEvent({
        type: decoded.type,
        id: decoded.id,
        timestamp: decoded.timestamp,
        correlationId: decoded.correlationId ?? null,
        payload: decoded.payload,
      });
    } catch {
      /* ignore malformed frames; the runtime is the authority */
    }
  });

  next.addEventListener('close', () => {
    if (socket !== next) return;
    socket = null;
    settleOpenWaiters(false);
    notifyStatus(false);
    scheduleRetry();
  });

  next.addEventListener('error', () => {
    // 'close' always follows 'error', and that's where the retry is scheduled.
    if (socket === next) notifyStatus(false);
  });
}

function scheduleRetry(): void {
  if (!desired || retryTimer !== null) return;
  const delay = RETRY_SCHEDULE_MS[retryAttempt] ?? MAX_RETRY_MS;
  retryAttempt = Math.min(retryAttempt + 1, RETRY_SCHEDULE_MS.length);
  retryTimer = window.setTimeout(() => {
    retryTimer = null;
    openSocket();
  }, delay);
}

function clearRetry(): void {
  if (retryTimer === null) return;
  window.clearTimeout(retryTimer);
  retryTimer = null;
}

function settleOpenWaiters(connected: boolean): void {
  if (openWaiters.length === 0) return;
  const waiters = openWaiters;
  openWaiters = [];
  for (const waiter of waiters) {
    try {
      waiter(connected);
    } catch {
      /* a broken waiter must not block the others */
    }
  }
}

function notifyStatus(connected: boolean): void {
  for (const listener of statusListeners) {
    try {
      listener(connected);
    } catch {
      /* don't let one broken consumer kill delivery to others */
    }
  }
}

function notifyReconnect(): void {
  for (const listener of reconnectListeners) {
    try {
      listener();
    } catch {
      /* don't let one broken consumer kill delivery to others */
    }
  }
}

/** Test-only reset so suites don't leak sockets or listeners between cases. */
export function __resetRuntimeSocketForTests(): void {
  disconnectRuntimeSocket();
  statusListeners.clear();
  reconnectListeners.clear();
  retryAttempt = 0;
  hasEverOpened = false;
}
