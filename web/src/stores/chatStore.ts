import { create } from 'zustand';
import type {
  ChatMessage,
  ChatThread,
  ChatTurnComplete,
  ChatTurnDelta,
  ChatTurnStart,
  ChatRunStateChanged,
  ChatUserMessageAppended,
  RuntimeEvent,
  ThreadSummary,
  TurnRunSnapshot,
} from '@thaddeus/shared-types';
import { ChatTurnEventTypes } from '@thaddeus/shared-types';
import * as api from '../lib/chatApi';
import type { WikiChatContextInput, WikiMutationTargetInput } from '../lib/chatApi';
import { ensureRuntimeSocket, subscribeRuntimeReconnect } from '../lib/runtimeSocket';
import { subscribeWsEvents } from '../lib/wsEvents';
import { useMemoryRecallStore } from './memoryRecallStore';
import { useToolActivityStore } from './toolActivityStore';

/**
 * Single-threaded (one active conversation at a time) chat store. Reads thread
 * metadata via REST, then consumes /ws turn events off the shared runtime event
 * bus to render the assistant reply incrementally. The active reply is tracked
 * separately from persisted messages so the UI can render in-progress text
 * without mutating the durable messages array.
 */

interface ActiveTurn {
  messageId: string;
  text: string;
  cancelled: boolean;
}

interface ChatStoreState {
  threads: ThreadSummary[];
  activeThreadId: string | null;
  activeThread: ChatThread | null;
  activeTurn: ActiveTurn | null;
  activeRun: TurnRunSnapshot | null;
  loading: boolean;
  error: string | null;
  sending: boolean;
  /**
   * Epoch ms of the local submit, set synchronously so the progress surface can
   * render on the same frame the user hits Send instead of waiting a round-trip
   * for `chat.turn.start`. Cleared when the turn completes or fails.
   */
  pendingSince: number | null;

  loadThreads: () => Promise<void>;
  openThread: (id: string) => Promise<void>;
  newThread: (title?: string) => Promise<ChatThread>;
  updateThread: (id: string, patch: { title?: string; pinned?: boolean }) => Promise<ChatThread>;
  deleteThread: (id: string) => Promise<void>;
  send: (
    text: string,
    wikiContext?: WikiChatContextInput,
    options?: { ephemeralMemory?: boolean; wikiMutationTarget?: WikiMutationTargetInput },
  ) => Promise<void>;
  retryLatestResponse: (options?: { ephemeralMemory?: boolean }) => Promise<void>;
  pauseActiveRun: () => Promise<void>;
  resumeActiveRun: () => Promise<void>;
  cancelActiveRun: () => Promise<void>;
  takeOverActiveRun: () => Promise<void>;
  redirectActiveRun: (instruction: string) => Promise<void>;
  approveActivePlan: () => Promise<void>;
  editActivePlan: (steps: import('@thaddeus/shared-types').WorkPlanStep[]) => Promise<void>;
  /**
   * Re-reads the active thread and its runs from REST. Called after a socket
   * reconnect so a turn that finished while the socket was down can't leave the
   * UI stuck on a progress card forever.
   */
  resyncActiveThread: () => Promise<void>;
  destroy: () => void;
  ingestEvent: (evt: RuntimeEvent<unknown>) => void;
}

let unsubscribeBus: (() => void) | null = null;
let unsubscribeReconnect: (() => void) | null = null;

/**
 * Attaches to the shared runtime event bus (idempotent) and starts the socket.
 * Returns a promise that resolves once the socket is actually open, so callers
 * that are about to trigger server-side work can avoid racing the turn events.
 */
function ensureSubscribed(): Promise<boolean> {
  unsubscribeBus ??= subscribeWsEvents((evt) => {
    useChatStore.getState().ingestEvent(evt as RuntimeEvent<unknown>);
  });
  unsubscribeReconnect ??= subscribeRuntimeReconnect(() => {
    void useChatStore.getState().resyncActiveThread();
  });
  return ensureRuntimeSocket();
}

export const useChatStore = create<ChatStoreState>((set, get) => ({
  threads: [],
  activeThreadId: null,
  activeThread: null,
  activeTurn: null,
  activeRun: null,
  loading: false,
  error: null,
  sending: false,
  pendingSince: null,

  loadThreads: async () => {
    set({ loading: true, error: null });
    try {
      const threads = await api.listThreads();
      set({ threads, loading: false });
    } catch (e) {
      set({ error: (e as Error).message, loading: false });
    }
  },

  openThread: async (id: string) => {
    // Subscribe and dial before the REST reads. The socket handshake then
    // overlaps the thread fetch instead of starting after it, so a send that
    // immediately follows an open (the new-conversation flow) finds the socket
    // already up rather than racing `chat.turn.start`.
    const connecting = ensureSubscribed();
    set({
      loading: true,
      activeThreadId: id,
      activeThread: null,
      activeTurn: null,
      activeRun: null,
      pendingSince: null,
    });
    try {
      const [thread, runs] = await Promise.all([api.getThread(id), api.listRuns(id)]);
      const activeRun = runs.find((run) => !isTerminalRun(run.state)) ?? null;
      set({ activeThread: thread, activeRun, loading: false });
      // Case-insensitive: the wire format is "Assistant", so the obvious
      // `m.role === 'assistant'` matched nothing and every reopened thread came
      // back with its tool pills and memory chips missing.
      const assistantMessageIds = thread.messages
        .filter((m) => isRole(m.role, 'assistant'))
        .map((m) => m.id);
      void useMemoryRecallStore.getState().hydrateFromTraces(assistantMessageIds);
      void useToolActivityStore.getState().hydrateFromTraces(assistantMessageIds);
      await connecting;
    } catch (e) {
      set({ error: (e as Error).message, loading: false });
    }
  },

  newThread: async (title?: string) => {
    // Mirror the try/catch-to-error shape the rest of the store uses so
    // failures don't bubble up as unhandled promise rejections. Home's
    // Send button needs a consumable error state, not a silent drop.
    set({ error: null });
    try {
      const thread = await api.createThread(title);
      set((s) => ({
        threads: [
          {
            id: thread.id,
            title: thread.title,
            createdAt: thread.createdAt,
            updatedAt: thread.updatedAt,
            messageCount: 0,
          },
          ...s.threads,
        ],
      }));
      return thread;
    } catch (e) {
      set({ error: (e as Error).message });
      throw e;
    }
  },

  updateThread: async (id, patch) => {
    set({ error: null });
    try {
      const thread = await api.patchThread(id, patch);
      set((state) => ({
        threads: state.threads.map((summary) => summary.id === id
          ? {
              ...summary,
              title: thread.title,
              updatedAt: thread.updatedAt,
              pinned: thread.pinned ?? false,
            }
          : summary),
        activeThread: state.activeThreadId === id ? thread : state.activeThread,
      }));
      return thread;
    } catch (e) {
      set({ error: (e as Error).message });
      throw e;
    }
  },

  deleteThread: async (id) => {
    set({ error: null });
    try {
      await api.deleteThread(id);
      set((state) => ({
        threads: state.threads.filter((thread) => thread.id !== id),
        activeThreadId: state.activeThreadId === id ? null : state.activeThreadId,
        activeThread: state.activeThreadId === id ? null : state.activeThread,
        activeTurn: state.activeThreadId === id ? null : state.activeTurn,
        activeRun: state.activeThreadId === id ? null : state.activeRun,
      }));
    } catch (e) {
      set({ error: (e as Error).message });
      throw e;
    }
  },

  send: async (text, wikiContext, options) => {
    const id = get().activeThreadId;
    const trimmed = text.trim();
    if (!id || !trimmed) return;
    const optimisticId = `optimistic-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    const createdAt = new Date().toISOString();
    const optimisticMessage: ChatMessage = {
      id: optimisticId,
      role: 'user',
      text: trimmed,
      createdAt,
    };

    // Paint the user's bubble and the "working" surface synchronously. The
    // network round-trip and the socket handshake both happen after this, so
    // Send never looks like it did nothing.
    set((s) => {
      const thread = s.activeThread;
      return {
        sending: true,
        pendingSince: Date.now(),
        error: null,
        activeThread: thread && s.activeThreadId === id
          ? {
              ...thread,
              messages: [...thread.messages, optimisticMessage],
              updatedAt: createdAt,
            }
          : thread,
      };
    });
    // Wait for the socket before asking the runtime to start a turn — the
    // broadcaster has no replay buffer, so a `chat.turn.start` emitted before
    // the handshake completes is lost and the turn renders no streamed text.
    await ensureSubscribed();
    try {
      const result = await api.appendMessage(id, trimmed, wikiContext, options);
      set((s) => s.activeThreadId === id
        ? {
            activeThread: adoptServerThread(s.activeThread, result.thread),
            activeRun: result.run,
            sending: false,
          }
        // The user navigated to another conversation while the append was in
        // flight. Applying this snapshot would show them the wrong thread.
        : { sending: false });
    } catch (e) {
      set((s) => ({
        error: (e as Error).message,
        sending: false,
        pendingSince: null,
        activeThread: removeOptimisticMessage(s.activeThread, optimisticId),
      }));
    }
  },

  retryLatestResponse: async (options) => {
    const id = get().activeThreadId;
    if (!id || get().sending || get().activeTurn) return;
    set({ sending: true, pendingSince: Date.now(), error: null });
    await ensureSubscribed();
    try {
      const result = await api.retryLatestResponse(id, options);
      set((s) => s.activeThreadId === id
        ? {
            activeThread: adoptServerThread(s.activeThread, result.thread),
            activeRun: result.run,
            sending: false,
          }
        : { sending: false });
    } catch (e) {
      set({ error: (e as Error).message, sending: false, pendingSince: null });
    }
  },

  pauseActiveRun: async () => {
    const run = get().activeRun;
    if (!run || isTerminalRun(run.state)) return;
    const updated = await api.pauseRun(run.runId);
    set({ activeRun: updated });
  },

  resumeActiveRun: async () => {
    const run = get().activeRun;
    if (!run || isTerminalRun(run.state)) return;
    const updated = await api.resumeRun(run.runId);
    set({ activeRun: updated });
  },

  cancelActiveRun: async () => {
    const run = get().activeRun;
    if (!run || isTerminalRun(run.state)) return;
    const updated = await api.cancelRun(run.runId);
    set({ activeRun: updated });
  },

  takeOverActiveRun: async () => {
    const run = get().activeRun;
    if (!run || isTerminalRun(run.state)) return;
    const updated = await api.takeOverRun(run.runId);
    set({ activeRun: updated });
  },

  redirectActiveRun: async (instruction) => {
    const run = get().activeRun;
    if (!run || isTerminalRun(run.state) || !instruction.trim()) return;
    const updated = await api.redirectRun(run.runId, instruction.trim());
    set({ activeRun: updated });
  },

  approveActivePlan: async () => {
    const run = get().activeRun;
    if (!run?.plan || !isAwaitingApproval(run.state)) return;
    const updated = await api.approvePlan(run.runId, run.plan.version);
    set({ activeRun: updated });
  },

  editActivePlan: async (steps) => {
    const run = get().activeRun;
    if (!run?.plan || !isAwaitingApproval(run.state)) return;
    const updated = await api.editPlan(run.runId, run.plan.version, steps);
    set({ activeRun: updated });
  },

  resyncActiveThread: async () => {
    const id = get().activeThreadId;
    if (!id) return;
    try {
      const [thread, runs] = await Promise.all([api.getThread(id), api.listRuns(id)]);
      const activeRun = runs.find((run) => !isTerminalRun(run.state)) ?? null;
      set((s) => {
        // Nothing still running server-side means any progress surface we're
        // showing is stale — the completion event arrived while we were
        // disconnected. Clear it and take the persisted messages as truth.
        const stillWorking = activeRun !== null;
        return {
          activeThread: adoptServerThread(s.activeThread, thread),
          activeRun,
          activeTurn: stillWorking ? s.activeTurn : null,
          pendingSince: stillWorking ? s.pendingSince : null,
          sending: stillWorking ? s.sending : false,
        };
      });
    } catch {
      // Best effort: the socket just came back, so the next event or the next
      // user action will resolve state anyway.
    }
  },

  destroy: () => {
    unsubscribeBus?.();
    unsubscribeBus = null;
    unsubscribeReconnect?.();
    unsubscribeReconnect = null;
    set({
      activeThreadId: null,
      activeThread: null,
      activeTurn: null,
      activeRun: null,
      threads: [],
      loading: false,
      error: null,
      sending: false,
      pendingSince: null,
    });
  },

  ingestEvent: (evt) => {
    if (evt.type === ChatTurnEventTypes.RunStateChanged) {
      const p = evt.payload as ChatRunStateChanged;
      if (p.threadId !== get().activeThreadId) return;
      const normalized = {
        ...p,
        state: String(p.state).toLowerCase(),
      } as TurnRunSnapshot;
      set((state) => {
        if (state.activeRun?.runId === normalized.runId &&
            state.activeRun.version > normalized.version) return {};
        // A run that fails (rather than completing) never emits
        // `chat.turn.complete`, so without this the work surface would spin
        // forever on a failed turn.
        if (isTerminalRun(normalized.state) && !state.activeTurn) {
          return { activeRun: normalized, pendingSince: null, sending: false };
        }
        return { activeRun: normalized };
      });
      return;
    }
    if (evt.type === ChatTurnEventTypes.UserMessageAppended) {
      // A user message was appended server-side (e.g. an automation step).
      // The HTTP-POST path already renders the bubble optimistically, so we
      // dedupe by id here — only insert if this message isn't in the thread
      // already.
      const p = evt.payload as ChatUserMessageAppended;
      if (p.threadId !== get().activeThreadId) return;
      set((s) => {
        const thread = s.activeThread;
        if (!thread) return {};
        if (thread.messages.some((m) => m.id === p.messageId)) return {};
        const appended: ChatMessage = {
          id: p.messageId,
          role: 'user',
          text: p.text,
          createdAt: p.createdAt,
        };
        const withoutMatchingOptimistic = thread.messages.filter((m) =>
          !(m.id.startsWith('optimistic-') && isRole(m.role, 'user') && m.text === p.text),
        );
        return {
          activeThread: {
            ...thread,
            messages: [...withoutMatchingOptimistic, appended],
            updatedAt: p.createdAt,
          },
        };
      });
      return;
    }
    if (evt.type === ChatTurnEventTypes.Start) {
      const p = evt.payload as ChatTurnStart;
      if (p.threadId !== get().activeThreadId) return;
      set((s) => ({
        activeTurn: { messageId: p.messageId, text: '', cancelled: false },
        // The turn is real now; keep the original submit time so the elapsed
        // counter reflects the user's wait, not the server's start.
        pendingSince: s.pendingSince ?? Date.now(),
      }));
      return;
    }
    if (evt.type === ChatTurnEventTypes.Delta) {
      const p = evt.payload as ChatTurnDelta;
      if (p.threadId !== get().activeThreadId) return;
      set((s) => {
        // A delta with no active turn means we missed `chat.turn.start` (socket
        // still connecting, or a reconnect landed mid-turn). Adopt the turn from
        // the delta rather than dropping the text — silently discarding here is
        // what made replies appear all at once with no streaming.
        if (!s.activeTurn) {
          return {
            activeTurn: { messageId: p.messageId, text: p.text, cancelled: false },
            pendingSince: s.pendingSince ?? Date.now(),
          };
        }
        if (s.activeTurn.messageId !== p.messageId) return {};
        return { activeTurn: { ...s.activeTurn, text: s.activeTurn.text + p.text } };
      });
      return;
    }
    if (evt.type === ChatTurnEventTypes.Complete) {
      const p = evt.payload as ChatTurnComplete;
      if (p.threadId !== get().activeThreadId) return;
      const finalMessage: ChatMessage = {
        id: p.messageId,
        role: 'assistant',
        text: p.finalText,
        createdAt: p.completedAt,
        sources: p.sources ?? null,
      };
      set((s) => {
        const thread = s.activeThread;
        if (!thread) return { activeTurn: null, pendingSince: null, sending: false };
        const alreadyHasIt = thread.messages.some((m) => m.id === finalMessage.id);
        const messages = alreadyHasIt
          ? thread.messages.map((m) =>
              m.id === finalMessage.id
                ? { ...m, text: finalMessage.text, createdAt: finalMessage.createdAt, sources: finalMessage.sources }
                : m,
            )
          : [...thread.messages, finalMessage];
        return {
          activeThread: { ...thread, messages, updatedAt: p.completedAt },
          activeTurn: null,
          pendingSince: null,
          sending: false,
        };
      });
    }
  },
}));

/**
 * Compares a message role case-insensitively.
 *
 * `ChatRole` is declared as `"user" | "assistant" | "system"`, but the runtime
 * serializes the C# enum as `"User"` / `"Assistant"` / `"System"`. TypeScript
 * therefore accepts `m.role === 'user'` while it silently never matches on real
 * data — which is why the render path spells out `String(m.role).toLowerCase()`
 * everywhere. Route every role check through here instead of trusting the type.
 */
function isRole(role: ChatMessage['role'], expected: 'user' | 'assistant' | 'system'): boolean {
  return String(role || '').toLowerCase() === expected;
}

function isTerminalRun(state: TurnRunSnapshot['state']): boolean {
  return state === 'cancelled' || state === 'completed' || state === 'failed';
}

function isAwaitingApproval(state: TurnRunSnapshot['state']): boolean {
  return state === 'awaitingapproval' || state === 'awaiting_approval';
}

/**
 * Folds a server thread snapshot into what we already have on screen.
 *
 * A plain overwrite loses messages when the turn outruns its own HTTP response:
 * the assistant can finish (appending its message via `chat.turn.complete`)
 * before `POST /messages` resolves, and that response body predates the reply.
 * Union by id, server order first, and drop any optimistic user bubble the
 * server has now persisted under a real id.
 */
function adoptServerThread(local: ChatThread | null, server: ChatThread): ChatThread {
  if (!local || local.id !== server.id) return server;

  const serverIds = new Set(server.messages.map((m) => m.id));
  const serverUserTexts = new Set(
    server.messages.filter((m) => isRole(m.role, 'user')).map((m) => m.text),
  );
  const extras = local.messages.filter((m) => {
    if (serverIds.has(m.id)) return false;
    if (m.id.startsWith('optimistic-') && isRole(m.role, 'user')) {
      return !serverUserTexts.has(m.text);
    }
    return true;
  });

  if (extras.length === 0) return server;
  return { ...server, messages: [...server.messages, ...extras] };
}

function removeOptimisticMessage(thread: ChatThread | null, messageId: string): ChatThread | null {
  if (!thread) return thread;
  const messages = thread.messages.filter((m) => m.id !== messageId);
  return messages.length === thread.messages.length ? thread : { ...thread, messages };
}
