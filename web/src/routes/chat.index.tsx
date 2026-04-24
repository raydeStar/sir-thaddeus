import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useEffect } from 'react';
import { Plus } from 'lucide-react';
import { PageScaffold } from '../components/PageScaffold';
import { useChatStore } from '../stores/chatStore';

export const Route = createFileRoute('/chat/')({
  component: ChatListRoute,
});

function ChatListRoute() {
  const navigate = useNavigate();
  const threads = useChatStore((s) => s.threads);
  const loading = useChatStore((s) => s.loading);
  const error = useChatStore((s) => s.error);
  const loadThreads = useChatStore((s) => s.loadThreads);
  const newThread = useChatStore((s) => s.newThread);

  useEffect(() => {
    void loadThreads();
  }, [loadThreads]);

  const onNew = async () => {
    const t = await newThread();
    void navigate({ to: '/chat/$threadId', params: { threadId: t.id } });
  };

  return (
    <PageScaffold
      testId="route-chat"
      title="Chat"
      subtitle="Your conversations with Sir Thaddeus."
    >
      {/* Header bar only renders when there are conversations — the empty
          state has its own primary CTA, so we don't double the New chat
          button. */}
      {threads.length > 0 ? (
        <div className="mb-8 flex items-center justify-between">
          <span className="text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle" data-testid="chat-thread-count">
            {threads.length} {threads.length === 1 ? 'conversation' : 'conversations'}
          </span>
          <button
            type="button"
            onClick={onNew}
            data-testid="chat-new-thread"
            className="inline-flex items-center gap-1.5 rounded-full bg-accent px-4 py-2 text-sm font-medium text-white transition hover:opacity-90"
          >
            <Plus className="h-4 w-4" strokeWidth={2} />
            New chat
          </button>
        </div>
      ) : null}

      {loading ? <p className="text-sm text-ink-muted">Loading…</p> : null}
      {error ? (
        <p className="text-sm text-rose-500" data-testid="chat-error">
          {error}
        </p>
      ) : null}

      {!loading && threads.length === 0 ? (
        <div
          data-testid="chat-empty"
          className="flex flex-col items-center gap-3 py-20 text-center"
        >
          <p className="text-base font-medium text-ink">No conversations yet</p>
          <p className="max-w-sm text-sm text-ink-muted">
            Start one and Sir Thaddeus will keep the thread here for next time.
          </p>
          <button
            type="button"
            onClick={onNew}
            className="mt-3 inline-flex items-center gap-1.5 rounded-full bg-accent px-4 py-2 text-sm font-medium text-white hover:opacity-90"
          >
            <Plus className="h-4 w-4" strokeWidth={2} />
            New chat
          </button>
        </div>
      ) : null}

      {threads.length > 0 ? (
        <ul
          className="divide-y divide-line"
          data-testid="chat-thread-list"
        >
          {threads.map((t) => (
            <li key={t.id}>
              <Link
                to="/chat/$threadId"
                params={{ threadId: t.id }}
                className="block py-4 transition-colors hover:text-accent"
                data-testid={`chat-thread-${t.id}`}
              >
                <div className="flex items-baseline justify-between gap-3">
                  <span className="truncate text-[15px] font-medium text-ink group-hover:text-accent">
                    {t.title || 'Untitled conversation'}
                  </span>
                  <span className="shrink-0 text-[11px] text-ink-subtle">
                    {new Date(t.updatedAt).toLocaleString(undefined, {
                      month: 'short',
                      day: 'numeric',
                      hour: 'numeric',
                      minute: '2-digit',
                    })}
                  </span>
                </div>
                {t.lastMessagePreview ? (
                  <p className="mt-1 line-clamp-1 text-sm text-ink-muted">{t.lastMessagePreview}</p>
                ) : (
                  <p className="mt-1 text-sm text-ink-subtle">Empty thread</p>
                )}
              </Link>
            </li>
          ))}
        </ul>
      ) : null}
    </PageScaffold>
  );
}
