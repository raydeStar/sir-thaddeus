import { createFileRoute, useNavigate, Link } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { ChevronRight, MessageSquare, Sparkles } from 'lucide-react';
import { useChatStore } from '../stores/chatStore';
import { ChatComposer, type WikiContextSelection } from '../components/ChatComposer';

export const Route = createFileRoute('/')({
  component: HomeRoute,
});

function HomeRoute() {
  const navigate = useNavigate();
  const newThread = useChatStore((s) => s.newThread);
  const send = useChatStore((s) => s.send);
  const threads = useChatStore((s) => s.threads);
  const loadThreads = useChatStore((s) => s.loadThreads);
  const storeError = useChatStore((s) => s.error);

  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const [localError, setLocalError] = useState<string | null>(null);

  useEffect(() => {
    void loadThreads();
  }, [loadThreads]);

  const start = async (text: string, wikiContext?: WikiContextSelection) => {
    if (busy) return;
    setBusy(true);
    setLocalError(null);
    try {
      const t = await newThread();
      void navigate({ to: '/chat/$threadId', params: { threadId: t.id } });
      await useChatStore.getState().openThread(t.id);
      await send(text, wikiContext);
      setDraft('');
    } catch (e) {
      // Surface the failure so the user doesn't hit Send and see nothing
      // happen. Common causes: backend offline, auth token missing (when
      // opened directly in a browser), proxy misconfig.
      setLocalError((e as Error).message || 'Could not send your message.');
    } finally {
      setBusy(false);
    }
  };

  const displayError = localError ?? storeError;
  const recent = threads.slice(0, 6);

  return (
    <section
      data-testid="route-home"
      className="mx-auto flex min-h-full w-full max-w-[680px] flex-col px-6 pt-20 pb-16 md:pt-28"
    >
      {/* Hero mark — small, calm. Signals identity without being loud. */}
      <div
        className="mx-auto mb-10 flex h-11 w-11 items-center justify-center rounded-2xl bg-accent-soft text-accent shadow-[0_8px_24px_-12px_rgba(217,119,87,0.55)]"
        aria-hidden
      >
        <Sparkles className="h-5 w-5" strokeWidth={1.6} />
      </div>

      {/* Single-line headline. The app's strongest surface — one big sentence. */}
      <h1 className="text-center text-[40px] font-semibold leading-[1.08] tracking-[-0.03em] text-ink">
        How can I help?
      </h1>
      <p className="mt-3 text-center text-[15px] text-ink-muted">
        Ask anything, or pick up where you left off.
      </p>

      <div className="mt-10">
        <ChatComposer
          value={draft}
          onChange={setDraft}
          onSubmit={start}
          sending={busy}
          inputTestId="home-prompt"
          sendTestId="home-send"
          autoFocus
        />
        <p className="mt-3 text-center text-[11px] text-ink-subtle">
          Press <kbd className="rounded bg-canvas-sunken px-1.5 py-0.5 font-mono text-[10px]">Enter</kbd> to send
          <span className="mx-2">·</span>
          <kbd className="rounded bg-canvas-sunken px-1.5 py-0.5 font-mono text-[10px]">Shift</kbd>
          <span className="mx-1">+</span>
          <kbd className="rounded bg-canvas-sunken px-1.5 py-0.5 font-mono text-[10px]">Enter</kbd> for newline
        </p>
        {displayError ? (
          <div
            role="alert"
            data-testid="home-send-error"
            className="mt-3 rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-[13px] text-red-700 dark:text-red-300"
          >
            {displayError}
          </div>
        ) : null}
      </div>

      {/* Recents. Only renders when there are threads — otherwise the hero breathes. */}
      {recent.length > 0 ? (
        <nav aria-label="Recent conversations" className="mt-20">
          {/* Hairline divider gives the section its own visual weight so it
              doesn't read as a continuation of the input hint. */}
          <div className="mb-6 h-px bg-line" aria-hidden />
          <div className="mb-4 flex items-baseline justify-between">
            <p className="text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
              Recent
            </p>
            <Link
              to="/history"
              className="text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle transition-colors hover:text-accent"
            >
              View all
            </Link>
          </div>
          <ul className="space-y-1">
            {recent.map((t) => (
              <li key={t.id}>
                <Link
                  to="/chat/$threadId"
                  params={{ threadId: t.id }}
                  data-testid={`home-recent-${t.id}`}
                  className="group/recent flex items-center gap-3 rounded-xl px-3 py-2.5 text-sm text-ink transition-colors hover:bg-canvas-raised"
                >
                  <span
                    className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-canvas-sunken text-ink-subtle transition-colors group-hover/recent:bg-accent-soft group-hover/recent:text-accent"
                    aria-hidden
                  >
                    <MessageSquare className="h-3.5 w-3.5" strokeWidth={1.75} />
                  </span>
                  <span className="min-w-0 flex-1 truncate">
                    {t.title || 'Untitled conversation'}
                  </span>
                  <span className="shrink-0 text-xs tabular-nums text-ink-subtle">
                    {formatRelative(t.updatedAt)}
                  </span>
                  <ChevronRight
                    className="h-3.5 w-3.5 shrink-0 text-ink-subtle opacity-0 transition-opacity group-hover/recent:opacity-100"
                    strokeWidth={1.75}
                    aria-hidden
                  />
                </Link>
              </li>
            ))}
          </ul>
        </nav>
      ) : null}
    </section>
  );
}

function formatRelative(iso: string): string {
  try {
    const then = new Date(iso).getTime();
    const now = Date.now();
    const mins = Math.round((now - then) / 60_000);
    if (mins < 1) return 'just now';
    if (mins < 60) return `${mins}m ago`;
    const hrs = Math.round(mins / 60);
    if (hrs < 24) return `${hrs}h ago`;
    const days = Math.round(hrs / 24);
    if (days < 7) return `${days}d ago`;
    return new Date(iso).toLocaleDateString();
  } catch {
    return '';
  }
}
