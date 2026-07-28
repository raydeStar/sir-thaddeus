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
import type { WikiChatContextInput } from '../lib/chatApi';
import { buildRuntimeWebSocketUrl, readRuntimeMetadata } from '../lib/runtime';
import { useMemoryRecallStore } from './memoryRecallStore';
import { useToolActivityStore } from './toolActivityStore';

/**
 * Single-threaded (one active conversation at a time) chat store. Reads thread
 * metadata via REST, then subscribes to /ws turn events to render the assistant
 * reply incrementally. The active reply is tracked separately from persisted
 * messages so the UI can render in-progress text without mutating the durable
 * messages array.
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

  loadThreads: () => Promise<void>;
  openThread: (id: string) => Promise<void>;
  newThread: (title?: string) => Promise<ChatThread>;
  updateThread: (id: string, patch: { title?: string; pinned?: boolean }) => Promise<ChatThread>;
  deleteThread: (id: string) => Promise<void>;
  send: (
    text: string,
    wikiContext?: WikiChatContextInput,
    options?: { ephemeralMemory?: boolean },
  ) => Promise<void>;
  retryLatestResponse: (options?: { ephemeralMemory?: boolean }) => Promise<void>;
  pauseActiveRun: () => Promise<void>;
  resumeActiveRun: () => Promise<void>;
  cancelActiveRun: () => Promise<void>;
  takeOverActiveRun: () => Promise<void>;
  redirectActiveRun: (instruction: string) => Promise<void>;
  approveActivePlan: () => Promise<void>;
  editActivePlan: (steps: import('@thaddeus/shared-types').WorkPlanStep[]) => Promise<void>;
  destroy: () => void;
  ingestEvent: (evt: RuntimeEvent<unknown>) => void;
}

let socket: WebSocket | null = null;

function ensureSocket(onMessage: (evt: RuntimeEvent<unknown>) => void): void {
  if (socket) return;
  const { token } = readRuntimeMetadata();
  const url = buildRuntimeWebSocketUrl(token);
  if (!url) return;
  try {
    socket = new WebSocket(url);
  } catch {
    return;
  }
  socket.addEventListener('message', (msg) => {
    try {
      const evt = JSON.parse(msg.data as string) as RuntimeEvent<unknown>;
      onMessage(evt);
    } catch {
      /* ignore malformed frames */
    }
  });
  socket.addEventListener('close', () => {
    socket = null;
  });
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
    set({ loading: true, activeThreadId: id, activeThread: null, activeTurn: null, activeRun: null });
    try {
      const [thread, runs] = await Promise.all([api.getThread(id), api.listRuns(id)]);
      const activeRun = runs.find((run) => !isTerminalRun(run.state)) ?? null;
      set({ activeThread: thread, activeRun, loading: false });
      const assistantMessageIds = thread.messages
        .filter((m) => m.role === 'assistant')
        .map((m) => m.id);
      void useMemoryRecallStore.getState().hydrateFromTraces(assistantMessageIds);
      void useToolActivityStore.getState().hydrateFromTraces(assistantMessageIds);
      ensureSocket((evt) => get().ingestEvent(evt));
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

    set((s) => {
      const thread = s.activeThread;
      return {
        sending: true,
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
    ensureSocket((evt) => get().ingestEvent(evt));
    try {
      const result = await api.appendMessage(id, trimmed, wikiContext, options);
      set({ activeThread: result.thread, activeRun: result.run, sending: false });
    } catch (e) {
      set((s) => ({
        error: (e as Error).message,
        sending: false,
        activeThread: removeOptimisticMessage(s.activeThread, optimisticId),
      }));
    }
  },

  retryLatestResponse: async (options) => {
    const id = get().activeThreadId;
    if (!id || get().sending || get().activeTurn) return;
    set({ sending: true, error: null });
    ensureSocket((evt) => get().ingestEvent(evt));
    try {
      const result = await api.retryLatestResponse(id, options);
      set({ activeThread: result.thread, activeRun: result.run, sending: false });
    } catch (e) {
      set({ error: (e as Error).message, sending: false });
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

  destroy: () => {
    if (socket) socket.close();
    socket = null;
    set({
      activeThreadId: null,
      activeThread: null,
      activeTurn: null,
      activeRun: null,
      threads: [],
      loading: false,
      error: null,
      sending: false,
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
          !(m.id.startsWith('optimistic-') && m.role === 'user' && m.text === p.text),
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
      set({ activeTurn: { messageId: p.messageId, text: '', cancelled: false } });
      return;
    }
    if (evt.type === ChatTurnEventTypes.Delta) {
      const p = evt.payload as ChatTurnDelta;
      if (p.threadId !== get().activeThreadId) return;
      set((s) => {
        if (!s.activeTurn || s.activeTurn.messageId !== p.messageId) return {};
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
        if (!thread) return { activeTurn: null };
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
        };
      });
    }
  },
}));

function isTerminalRun(state: TurnRunSnapshot['state']): boolean {
  return state === 'cancelled' || state === 'completed' || state === 'failed';
}

function isAwaitingApproval(state: TurnRunSnapshot['state']): boolean {
  return state === 'awaitingapproval' || state === 'awaiting_approval';
}

function removeOptimisticMessage(thread: ChatThread | null, messageId: string): ChatThread | null {
  if (!thread) return thread;
  const messages = thread.messages.filter((m) => m.id !== messageId);
  return messages.length === thread.messages.length ? thread : { ...thread, messages };
}
