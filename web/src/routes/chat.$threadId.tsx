import { createFileRoute, Link } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { ArrowLeft, Plus } from 'lucide-react';
import { useChatStore } from '../stores/chatStore';
import { Markdown } from '../components/Markdown';
import { SourceCards } from '../components/SourceCards';
import { ToolActivityPills } from '../components/ToolActivityPills';
import { FootmanDecisionChip } from '../components/FootmanDecisionChip';
import { ChatComposer, type WikiContextSelection } from '../components/ChatComposer';
import type { ChatMessageSource } from '@thaddeus/shared-types';

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

  useEffect(() => {
    void openThread(threadId);
  }, [openThread, threadId]);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: 'smooth' });
  }, [thread?.messages.length, activeTurn?.text]);

  const onSubmit = async (text: string, wikiContext?: WikiContextSelection) => {
    if (sending) return;
    setDraft('');
    await send(text, wikiContext);
  };

  const messages = thread?.messages ?? [];
  const empty = messages.length === 0 && !activeTurn;

  return (
    <section
      data-testid="route-chat-thread"
      className="flex h-full flex-col"
    >
      {/* Ultra-thin header. The thread title is the content, not a chrome label. */}
      <div className="px-4 py-3 md:px-10">
        <div className="mx-auto flex w-full max-w-[720px] items-center gap-3">
          <Link
            to="/chat"
            className="flex h-7 w-7 items-center justify-center rounded-full text-ink-subtle transition-colors hover:text-ink"
            aria-label="Back to chats"
          >
            <ArrowLeft className="h-4 w-4" strokeWidth={1.75} />
          </Link>
          <h1 className="truncate text-[13px] font-medium text-ink-muted">
            {thread?.title ?? 'Loading…'}
          </h1>
        </div>
      </div>

      <div
        ref={scrollRef}
        data-testid="chat-message-list"
        className="flex-1 overflow-y-auto px-4 md:px-10"
      >
        <div className="mx-auto w-full max-w-[720px] py-6 pb-40">
          {empty ? (
            <div className="flex h-full items-center justify-center pt-24 text-center">
              <p className="text-sm text-ink-subtle" data-testid="chat-thread-empty">
                No messages yet. Say hello.
              </p>
            </div>
          ) : (
            <div className="space-y-8">
              {messages.map((m) => {
                const role = String(m.role || '').toLowerCase();
                if (role !== 'user' && !m.text?.trim()) return null;
                return (
                  <MessageRow
                    key={m.id}
                    role={role as MessageRowProps['role']}
                    text={m.text}
                    sources={m.sources ?? null}
                    messageId={m.id}
                    testId={`chat-message-${m.id}`}
                  />
                );
              })}
              {activeTurn ? (
                <MessageRow
                  role="assistant"
                  text={activeTurn.text || ''}
                  messageId={activeTurn.messageId}
                  streaming
                  testId="chat-message-streaming"
                />
              ) : null}
            </div>
          )}
        </div>
      </div>

      {/* Composer. Single rounded shape floating above a subtle top gradient. */}
      <div className="relative px-4 pb-6 pt-2 md:px-10">
        {/* Fade-out so long threads don't crash against the composer. */}
        <div
          aria-hidden
          className="pointer-events-none absolute inset-x-0 -top-10 h-10 bg-gradient-to-b from-transparent to-canvas"
        />
        <div className="mx-auto w-full max-w-[720px]">
          {error ? (
            <p
              role="alert"
              className="mb-2 rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-xs text-red-700 dark:text-red-300"
              data-testid="chat-thread-error"
            >
              {error}
            </p>
          ) : null}

          <ChatComposer
            value={draft}
            onChange={setDraft}
            onSubmit={onSubmit}
            sending={sending}
            inputTestId="chat-input"
            sendTestId="chat-send"
            rightActions={
              <Link
                to="/"
                className="chat-composer-icon-button"
                aria-label="New chat"
                title="New chat"
              >
                <Plus className="h-4 w-4" strokeWidth={1.9} />
              </Link>
            }
          />
        </div>
      </div>
    </section>
  );
}

interface MessageRowProps {
  role: 'user' | 'assistant' | 'system';
  text: string;
  sources?: ChatMessageSource[] | null;
  messageId?: string;
  streaming?: boolean;
  testId: string;
}

function MessageRow({ role, text, sources, messageId, streaming, testId }: MessageRowProps) {
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
        <div className="max-w-[82%] whitespace-pre-wrap rounded-3xl rounded-tr-lg bg-canvas-sunken px-4 py-2.5 text-[15px] leading-6 text-ink">
          {text}
        </div>
      </div>
    );
  }

  // Assistant messages flow into the page directly — no bubble, no avatar.
  // Tool activity pills (if any fired during this turn) float above the
  // text so the reader sees what the model did before reading what it said.
  return (
    <div
      data-testid={testId}
      data-role={role}
      data-streaming={streaming ? 'true' : undefined}
    >
      {messageId ? <FootmanDecisionChip messageId={messageId} /> : null}
      {messageId ? <ToolActivityPills messageId={messageId} /> : null}
      <Markdown>{text}</Markdown>
      {sources && sources.length > 0 ? <SourceCards sources={sources} /> : null}
      {streaming ? (
        <span
          className="ml-0.5 inline-block h-[1.1em] w-[2px] translate-y-1 animate-pulse bg-accent align-middle"
          aria-hidden
        />
      ) : null}
    </div>
  );
}
