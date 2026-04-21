import { createFileRoute, Link } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { ArrowLeft, ArrowUp } from 'lucide-react';
import { useChatStore } from '../stores/chatStore';

export const Route = createFileRoute('/chat/$threadId')({
  component: ChatThreadRoute,
});

function ChatThreadRoute() {
  const { threadId } = Route.useParams();
  const thread = useChatStore((s) => s.activeThread);
  const activeTurn = useChatStore((s) => s.activeTurn);
  const sending = useChatStore((s) => s.sending);
  const error = useChatStore((s) => s.error);
  const openThread = useChatStore((s) => s.openThread);
  const send = useChatStore((s) => s.send);

  const [draft, setDraft] = useState('');
  const scrollRef = useRef<HTMLDivElement>(null);
  const composerRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    void openThread(threadId);
  }, [openThread, threadId]);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: 'smooth' });
  }, [thread?.messages.length, activeTurn?.text]);

  // Auto-grow textarea.
  useEffect(() => {
    const el = composerRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 220)}px`;
  }, [draft]);

  const submit = async () => {
    if (!draft.trim() || sending) return;
    const text = draft;
    setDraft('');
    await send(text);
  };

  const onSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    await submit();
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void submit();
    }
  };

  const messages = thread?.messages ?? [];
  const empty = messages.length === 0 && !activeTurn;

  return (
    <section
      data-testid="route-chat-thread"
      className="flex h-full flex-col"
    >
      <div className="border-b border-line bg-canvas/80 px-4 py-3 backdrop-blur md:px-8">
        <div className="mx-auto flex w-full max-w-3xl items-center gap-3">
          <Link
            to="/chat"
            className="flex h-7 w-7 items-center justify-center rounded-full text-ink-muted hover:bg-accent-soft hover:text-ink"
            aria-label="Back to chats"
          >
            <ArrowLeft className="h-4 w-4" strokeWidth={1.75} />
          </Link>
          <h1 className="truncate text-sm font-medium text-ink">
            {thread?.title ?? 'Loading…'}
          </h1>
        </div>
      </div>

      <div
        ref={scrollRef}
        data-testid="chat-message-list"
        className="flex-1 overflow-y-auto px-4 md:px-8"
      >
        <div className="mx-auto w-full max-w-3xl py-8">
          {empty ? (
            <div className="flex h-full items-center justify-center pt-20 text-center">
              <p className="text-sm italic text-ink-subtle" data-testid="chat-thread-empty">
                No messages yet. Say hello.
              </p>
            </div>
          ) : (
            <div className="space-y-6">
              {messages.map((m) => {
                // Hide empty assistant placeholders the runtime sometimes emits
                // before the streamed turn lands.
                const role = String(m.role || '').toLowerCase();
                if (role !== 'user' && !m.text?.trim()) return null;
                return (
                  <MessageRow
                    key={m.id}
                    role={role as MessageRowProps['role']}
                    text={m.text}
                    testId={`chat-message-${m.id}`}
                  />
                );
              })}
              {activeTurn ? (
                <MessageRow
                  role="assistant"
                  text={activeTurn.text || '…'}
                  streaming
                  testId="chat-message-streaming"
                />
              ) : null}
            </div>
          )}
        </div>
      </div>

      <div className="border-t border-line bg-canvas px-4 pb-5 pt-3 md:px-8">
        <div className="mx-auto w-full max-w-3xl">
          {error ? (
            <p className="mb-2 text-xs text-rose-600" data-testid="chat-thread-error">
              {error}
            </p>
          ) : null}

          <form
            onSubmit={onSubmit}
            data-testid="chat-composer"
            className="surface flex items-end gap-2 px-3 py-2.5"
          >
            <textarea
              ref={composerRef}
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={onKeyDown}
              placeholder="Message Sir Thaddeus…"
              rows={1}
              data-testid="chat-input"
              disabled={sending}
              className="min-h-[28px] flex-1 resize-none border-0 bg-transparent px-2 py-1.5 text-[15px] leading-6 text-ink placeholder:text-ink-subtle focus:outline-none disabled:opacity-60"
            />
            <button
              type="submit"
              data-testid="chat-send"
              disabled={sending || !draft.trim()}
              aria-label="Send message"
              className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-accent text-white transition hover:opacity-90 disabled:opacity-30"
            >
              <ArrowUp className="h-4 w-4" strokeWidth={2.25} />
            </button>
          </form>

          <p className="mt-2 text-center text-[11px] text-ink-subtle">
            Sir Thaddeus runs locally. Press <kbd className="rounded bg-canvas-sunken px-1 py-0.5 font-mono text-[10px]">Enter</kbd> to send, <kbd className="rounded bg-canvas-sunken px-1 py-0.5 font-mono text-[10px]">Shift</kbd>+<kbd className="rounded bg-canvas-sunken px-1 py-0.5 font-mono text-[10px]">Enter</kbd> for newline.
          </p>
        </div>
      </div>
    </section>
  );
}

interface MessageRowProps {
  role: 'user' | 'assistant' | 'system';
  text: string;
  streaming?: boolean;
  testId: string;
}

function MessageRow({ role, text, streaming, testId }: MessageRowProps) {
  const normalized = String(role || '').toLowerCase();
  const isUser = normalized === 'user';

  if (isUser) {
    return (
      <div
        data-testid={testId}
        data-role={role}
        data-streaming={streaming ? 'true' : undefined}
        className="flex justify-end"
      >
        <div className="max-w-[78%] whitespace-pre-wrap rounded-2xl bg-accent-soft px-4 py-2.5 text-[15px] leading-6 text-ink">
          {text}
        </div>
      </div>
    );
  }

  return (
    <div
      data-testid={testId}
      data-role={role}
      data-streaming={streaming ? 'true' : undefined}
      className="group flex gap-3"
    >
      <div className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-accent text-[11px] font-semibold text-white">
        ST
      </div>
      <div className="flex-1 whitespace-pre-wrap pt-0.5 text-[15px] leading-7 text-ink">
        {text}
        {streaming ? (
          <span
            className="ml-1 inline-block h-3.5 w-[3px] translate-y-0.5 animate-pulse bg-ink"
            aria-hidden
          />
        ) : null}
      </div>
    </div>
  );
}
