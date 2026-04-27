import { createFileRoute, useNavigate, Link } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { ArrowUp, ChevronRight, CircleStop, Loader2, MessageSquare, Sparkles } from 'lucide-react';
import { useChatStore } from '../stores/chatStore';
import { stopAllProcesses } from '../lib/runtimeActions';

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
  const [stoppingAll, setStoppingAll] = useState(false);
  const [stopAllStatus, setStopAllStatus] = useState<string | null>(null);
  const [stopAllError, setStopAllError] = useState<string | null>(null);
  const [localError, setLocalError] = useState<string | null>(null);
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
    setLocalError(null);
    try {
      const t = await newThread();
      void navigate({ to: '/chat/$threadId', params: { threadId: t.id } });
      await useChatStore.getState().openThread(t.id);
      await send(draft.trim());
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

  const onStopAll = async () => {
    if (stoppingAll) return;
    setStoppingAll(true);
    setStopAllStatus(null);
    setStopAllError(null);
    try {
      const result = await stopAllProcesses();
      const stoppedCount = result.stopped?.length ?? 0;
      const errorCount = result.errors?.length ?? 0;
      setStopAllStatus(
        errorCount > 0
          ? `Stop requested. ${stoppedCount} stopped, ${errorCount} issue${errorCount === 1 ? '' : 's'} reported.`
          : stoppedCount > 0
            ? `Stopped ${stoppedCount} managed item${stoppedCount === 1 ? '' : 's'}.`
            : 'Stop requested. No managed sidecars were running.'
      );
    } catch (e) {
      setStopAllError((e as Error).message || 'Could not send stop-all command.');
    } finally {
      setStoppingAll(false);
    }
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void start();
    }
  };

  const recent = threads.slice(0, 6);

  return (
    <section
      data-testid="route-home"
      className="mx-auto flex min-h-full w-full max-w-[680px] flex-col px-6 pt-20 pb-16 md:pt-28"
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

      <div className="mt-8 flex flex-col items-center gap-2" aria-live="polite">
        <button
          type="button"
          onClick={onStopAll}
          disabled={stoppingAll}
          data-testid="home-stop-all"
          className="inline-flex min-h-14 items-center justify-center gap-3 rounded-lg border border-red-300 bg-red-600 px-8 py-4 text-base font-semibold uppercase tracking-[0.08em] text-white shadow-lg shadow-red-950/15 transition hover:bg-red-700 focus:outline-none focus:ring-4 focus:ring-red-500/25 disabled:cursor-not-allowed disabled:bg-red-900/60 disabled:text-white/70"
        >
          {stoppingAll ? (
            <Loader2 className="h-5 w-5 animate-spin" strokeWidth={2.25} aria-hidden />
          ) : (
            <CircleStop className="h-5 w-5" strokeWidth={2.25} aria-hidden />
          )}
          {stoppingAll ? 'Stopping' : 'STOP ALL'}
        </button>
        {stopAllStatus ? (
          <p className="max-w-md text-center text-xs text-ink-muted" data-testid="home-stop-all-status">
            {stopAllStatus}
          </p>
        ) : null}
        {stopAllError ? (
          <p className="max-w-md text-center text-xs text-rose-500" data-testid="home-stop-all-error">
            {stopAllError}
          </p>
        ) : null}
      </div>

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
        {displayError ? (
          <div
            role="alert"
            data-testid="home-send-error"
            className="mt-3 rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-[13px] text-red-700 dark:text-red-300"
          >
            {displayError}
          </div>
        ) : null}
      </form>

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
