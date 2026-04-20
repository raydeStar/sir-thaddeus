import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useEffect } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { useChatStore } from '../stores/chatStore';

export const Route = createFileRoute('/chat')({
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
      subtitle="Open a thread to continue a conversation, or start a new one."
    >
      <div className="mb-4 flex items-center justify-between">
        <span className="text-sm text-slate-500" data-testid="chat-thread-count">
          {threads.length} thread{threads.length === 1 ? '' : 's'}
        </span>
        <button
          type="button"
          onClick={onNew}
          data-testid="chat-new-thread"
          className="rounded-md bg-thaddeus-ink px-3 py-1.5 text-sm font-medium text-white hover:opacity-90"
        >
          New conversation
        </button>
      </div>

      {loading ? <p className="text-sm text-slate-500">Loading…</p> : null}
      {error ? (
        <p className="text-sm text-red-600" data-testid="chat-error">
          {error}
        </p>
      ) : null}

      {!loading && threads.length === 0 ? (
        <p className="text-sm text-slate-500" data-testid="chat-empty">
          No conversations yet. Click <span className="font-medium">New conversation</span> to start one.
        </p>
      ) : null}

      <ul className="divide-y divide-slate-200" data-testid="chat-thread-list">
        {threads.map((t) => (
          <li key={t.id}>
            <Link
              to="/chat/$threadId"
              params={{ threadId: t.id }}
              className="block py-3 hover:bg-slate-50"
              data-testid={`chat-thread-${t.id}`}
            >
              <div className="flex items-baseline justify-between">
                <span className="font-medium text-thaddeus-ink">{t.title}</span>
                <span className="text-xs text-slate-400">{new Date(t.updatedAt).toLocaleString()}</span>
              </div>
              {t.lastMessagePreview ? (
                <p className="mt-1 truncate text-sm text-slate-600">{t.lastMessagePreview}</p>
              ) : (
                <p className="mt-1 text-sm italic text-slate-400">empty thread</p>
              )}
            </Link>
          </li>
        ))}
      </ul>
    </PageScaffold>
  );
}
