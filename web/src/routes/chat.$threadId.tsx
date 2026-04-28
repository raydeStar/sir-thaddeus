import { createFileRoute, Link } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { ArrowLeft, ArrowUp, BookOpen } from 'lucide-react';
import { useChatStore } from '../stores/chatStore';
import { Markdown } from '../components/Markdown';
import { SourceCards } from '../components/SourceCards';
import { ToolActivityPills } from '../components/ToolActivityPills';
import { FootmanDecisionChip } from '../components/FootmanDecisionChip';
import type { ChatMessageSource } from '@thaddeus/shared-types';
import { getWikiTree, listWikiRoots } from '../lib/wikiApi';

interface WikiContextOption {
  pageId: string;
  title: string;
  rootName: string;
}

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
  const [wikiContextPageId, setWikiContextPageId] = useState('');
  const [wikiContextOptions, setWikiContextOptions] = useState<WikiContextOption[]>([]);
  const [wikiContextLoading, setWikiContextLoading] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);
  const composerRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    void openThread(threadId);
  }, [openThread, threadId]);

  useEffect(() => {
    let disposed = false;
    const loadWikiContextOptions = async () => {
      setWikiContextLoading(true);
      try {
        const roots = await listWikiRoots();
        const trees = await Promise.all(
          roots.map(async (root) => ({ root, tree: await getWikiTree(root.id) })),
        );
        if (disposed) return;
        setWikiContextOptions(
          trees.flatMap(({ root, tree }) =>
            tree.pages.map((page) => ({ pageId: page.id, title: page.title, rootName: root.name })),
          ),
        );
      } catch {
        if (!disposed) setWikiContextOptions([]);
      } finally {
        if (!disposed) setWikiContextLoading(false);
      }
    };

    void loadWikiContextOptions();
    return () => {
      disposed = true;
    };
  }, []);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: 'smooth' });
  }, [thread?.messages.length, activeTurn?.text]);

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
    await send(
      text,
      wikiContextPageId ? { mode: 'page', pageId: wikiContextPageId } : undefined,
    );
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
          className="pointer-events-none absolute inset-x-0 -top-8 h-8 bg-gradient-to-b from-transparent to-canvas"
        />
        <div className="mx-auto w-full max-w-[720px]">
          {error ? (
            <p className="mb-2 text-xs text-rose-500" data-testid="chat-thread-error">
              {error}
            </p>
          ) : null}

          <form
            onSubmit={onSubmit}
            data-testid="chat-composer"
            className="rounded-2xl border border-line bg-canvas-raised px-4 py-3 transition-colors focus-within:border-accent-ring focus-within:shadow-[0_0_0_4px_var(--color-accent-soft)]"
          >
            <div className="mb-2 flex items-center gap-2 border-b border-line/70 pb-2">
              <BookOpen className="h-4 w-4 text-ink-subtle" strokeWidth={1.75} aria-hidden />
              <select
                value={wikiContextPageId}
                onChange={(event) => setWikiContextPageId(event.target.value)}
                disabled={sending || wikiContextLoading || wikiContextOptions.length === 0}
                aria-label="Wiki context"
                data-testid="chat-wiki-context"
                className="min-w-0 flex-1 border-0 bg-transparent text-xs text-ink-muted outline-none focus:text-ink disabled:opacity-60"
              >
                <option value="">No wiki context</option>
                {wikiContextOptions.map((option) => (
                  <option key={option.pageId} value={option.pageId}>
                    {option.rootName} / {option.title}
                  </option>
                ))}
              </select>
            </div>
            <div className="flex items-end gap-2">
              <textarea
                ref={composerRef}
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={onKeyDown}
                placeholder="Message Sir Thaddeus…"
                rows={1}
                data-testid="chat-input"
                disabled={sending}
                className="min-h-[24px] max-h-[220px] flex-1 resize-none border-0 bg-transparent px-1 py-1 text-[15px] leading-6 text-ink placeholder:text-ink-subtle focus:outline-none disabled:opacity-60"
              />
              <button
                type="submit"
                data-testid="chat-send"
                disabled={sending || !draft.trim()}
                aria-label="Send message"
                className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-accent text-white transition hover:opacity-90 disabled:bg-line-strong disabled:text-ink-subtle"
              >
                <ArrowUp className="h-4 w-4" strokeWidth={2.25} />
              </button>
            </div>
          </form>
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
