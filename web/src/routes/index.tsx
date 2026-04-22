import { createFileRoute, useNavigate, Link } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { ArrowUp, Sparkles } from 'lucide-react';
import { useChatStore } from '../stores/chatStore';

export const Route = createFileRoute('/')({
  component: HomeRoute,
});

function HomeRoute() {
  const navigate = useNavigate();
  const newThread = useChatStore((s) => s.newThread);
  const send = useChatStore((s) => s.send);
  const threads = useChatStore((s) => s.threads);
  const loadThreads = useChatStore((s) => s.loadThreads);

  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    void loadThreads();
    textareaRef.current?.focus();
  }, [loadThreads]);

  // Auto-grow the textarea to fit content, capped so the page never gets overtaken.
  useEffect(() => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 240)}px`;
  }, [draft]);

  const start = async () => {
    if (!draft.trim() || busy) return;
    setBusy(true);
    try {
      const t = await newThread();
      void navigate({ to: '/chat/$threadId', params: { threadId: t.id } });
      await useChatStore.getState().openThread(t.id);
      await send(draft.trim());
      setDraft('');
    } finally {
      setBusy(false);
    }
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void start();
    }
  };

  const recent = threads.slice(0, 5);

  return (
    <section
      data-testid="route-home"
      className="mx-auto flex min-h-full w-full max-w-[680px] flex-col px-6 pt-24 pb-16 md:pt-32"
    >
      {/* Hero mark — small, calm. Signals identity without being loud. */}
      <div
        className="mx-auto mb-10 flex h-11 w-11 items-center justify-center rounded-2xl bg-accent-soft text-accent"
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

      {/* Prompt. Deliberately minimal — no visible card border until focus. */}
      <form
        onSubmit={(e) => {
          e.preventDefault();
          void start();
        }}
        className="mt-10"
      >
        <div
          className="group/prompt flex items-end gap-2 rounded-2xl border border-line bg-canvas-raised px-4 py-3 transition-colors focus-within:border-accent-ring focus-within:shadow-[0_0_0_4px_var(--color-accent-soft)]"
        >
          <textarea
            ref={textareaRef}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder="Message Sir Thaddeus…"
            rows={1}
            data-testid="home-prompt"
            className="min-h-[24px] max-h-[240px] flex-1 resize-none border-0 bg-transparent px-1 py-1 text-[15px] leading-6 text-ink placeholder:text-ink-subtle focus:outline-none"
          />
          <button
            type="submit"
            disabled={!draft.trim() || busy}
            aria-label="Send"
            data-testid="home-send"
            className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-accent text-white transition hover:opacity-90 disabled:bg-line-strong disabled:text-ink-subtle"
          >
            <ArrowUp className="h-4 w-4" strokeWidth={2.25} />
          </button>
        </div>
        <p className="mt-2 text-center text-[11px] text-ink-subtle">
          Press <kbd className="rounded bg-canvas-sunken px-1.5 py-0.5 font-mono text-[10px]">Enter</kbd> to send
          <span className="mx-2">·</span>
          <kbd className="rounded bg-canvas-sunken px-1.5 py-0.5 font-mono text-[10px]">Shift</kbd>
          <span className="mx-1">+</span>
          <kbd className="rounded bg-canvas-sunken px-1.5 py-0.5 font-mono text-[10px]">Enter</kbd> for newline
        </p>
      </form>

      {/* Recents. Only renders when there are threads — otherwise the hero breathes. */}
      {recent.length > 0 ? (
        <nav aria-label="Recent conversations" className="mt-16">
          <p className="mb-3 text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
            Recent
          </p>
          <ul className="divide-y divide-line">
            {recent.map((t) => (
              <li key={t.id}>
                <Link
                  to="/chat/$threadId"
                  params={{ threadId: t.id }}
                  data-testid={`home-recent-${t.id}`}
                  className="flex items-center justify-between gap-3 py-3 text-sm text-ink transition-colors hover:text-accent"
                >
                  <span className="min-w-0 truncate">
                    {t.title || 'Untitled conversation'}
                  </span>
                  <span className="shrink-0 text-xs text-ink-subtle">
                    {formatRelative(t.updatedAt)}
                  </span>
                </Link>
              </li>
            ))}
          </ul>
          <div className="mt-4">
            <Link
              to="/history"
              className="text-xs text-ink-muted hover:text-accent"
            >
              All conversations →
            </Link>
          </div>
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
