import { createFileRoute, Link } from '@tanstack/react-router';
import { useEffect, useMemo, useState } from 'react';
import type { ThreadSummary } from '@thaddeus/shared-types';
import { PageScaffold } from '../components/PageScaffold';
import * as api from '../lib/chatApi';

export const Route = createFileRoute('/history')({
  component: HistoryRoute,
});

function HistoryRoute() {
  const [threads, setThreads] = useState<ThreadSummary[]>([]);
  const [query, setQuery] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = async () => {
    setLoading(true);
    setError(null);
    try {
      setThreads(await api.listThreads());
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void refresh();
  }, []);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return threads;
    return threads.filter(
      (t) =>
        t.title.toLowerCase().includes(q) ||
        (t.lastMessagePreview ?? '').toLowerCase().includes(q),
    );
  }, [threads, query]);

  const pinned = filtered.filter((t) => t.pinned);
  const unpinned = filtered.filter((t) => !t.pinned);
  const grouped = groupByDay(unpinned);

  const togglePin = async (t: ThreadSummary) => {
    await api.patchThread(t.id, { pinned: !t.pinned });
    await refresh();
  };

  const rename = async (t: ThreadSummary) => {
    const next = window.prompt('Rename conversation', t.title);
    if (next === null) return;
    await api.patchThread(t.id, { title: next });
    await refresh();
  };

  const remove = async (t: ThreadSummary) => {
    if (!window.confirm(`Delete "${t.title}"? This cannot be undone.`)) return;
    await api.deleteThread(t.id);
    await refresh();
  };

  return (
    <PageScaffold testId="route-history" title="History" subtitle="Past chats, grouped by day.">
      <div className="mb-4">
        <input
          type="search"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search title or last message…"
          data-testid="history-search"
          className="w-full rounded-md border border-slate-300 px-3 py-2 text-sm focus:border-thaddeus-ink focus:outline-none"
        />
      </div>

      {loading ? <p className="text-sm text-slate-500">Loading…</p> : null}
      {error ? (
        <p className="text-sm text-red-600" data-testid="history-error">
          {error}
        </p>
      ) : null}

      {!loading && filtered.length === 0 ? (
        <p className="text-sm text-slate-500" data-testid="history-empty">
          {query.trim() ? 'No threads match your search.' : 'No conversations yet.'}
        </p>
      ) : null}

      {pinned.length > 0 ? (
        <Section title="Pinned" testId="history-pinned">
          {pinned.map((t) => (
            <ThreadRow
              key={t.id}
              thread={t}
              onPinToggle={togglePin}
              onRename={rename}
              onDelete={remove}
            />
          ))}
        </Section>
      ) : null}

      {grouped.map(([label, items]) => (
        <Section key={label} title={label} testId={`history-day-${slug(label)}`}>
          {items.map((t) => (
            <ThreadRow
              key={t.id}
              thread={t}
              onPinToggle={togglePin}
              onRename={rename}
              onDelete={remove}
            />
          ))}
        </Section>
      ))}
    </PageScaffold>
  );
}

interface SectionProps {
  title: string;
  testId: string;
  children: React.ReactNode;
}

function Section({ title, testId, children }: SectionProps) {
  return (
    <section data-testid={testId} className="mb-6">
      <h2 className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">{title}</h2>
      <ul className="divide-y divide-slate-200">{children}</ul>
    </section>
  );
}

interface ThreadRowProps {
  thread: ThreadSummary;
  onPinToggle: (t: ThreadSummary) => void | Promise<void>;
  onRename: (t: ThreadSummary) => void | Promise<void>;
  onDelete: (t: ThreadSummary) => void | Promise<void>;
}

function ThreadRow({ thread, onPinToggle, onRename, onDelete }: ThreadRowProps) {
  return (
    <li data-testid={`history-thread-${thread.id}`} className="flex items-start gap-3 py-3">
      <Link
        to="/chat/$threadId"
        params={{ threadId: thread.id }}
        className="flex-1 min-w-0 hover:bg-slate-50"
      >
        <div className="flex items-baseline justify-between gap-2">
          <span className="truncate font-medium text-thaddeus-ink">
            {thread.pinned ? '★ ' : ''}
            {thread.title}
          </span>
          <span className="shrink-0 text-xs text-slate-400">
            {new Date(thread.updatedAt).toLocaleString()}
          </span>
        </div>
        {thread.lastMessagePreview ? (
          <p className="mt-1 truncate text-sm text-slate-600">{thread.lastMessagePreview}</p>
        ) : (
          <p className="mt-1 text-sm italic text-slate-400">empty thread</p>
        )}
      </Link>
      <div className="flex shrink-0 gap-1">
        <button
          type="button"
          onClick={() => onPinToggle(thread)}
          data-testid={`history-pin-${thread.id}`}
          aria-label={thread.pinned ? 'Unpin' : 'Pin'}
          className="rounded px-2 py-1 text-xs text-slate-600 hover:bg-slate-100"
        >
          {thread.pinned ? 'Unpin' : 'Pin'}
        </button>
        <button
          type="button"
          onClick={() => onRename(thread)}
          data-testid={`history-rename-${thread.id}`}
          className="rounded px-2 py-1 text-xs text-slate-600 hover:bg-slate-100"
        >
          Rename
        </button>
        <button
          type="button"
          onClick={() => onDelete(thread)}
          data-testid={`history-delete-${thread.id}`}
          className="rounded px-2 py-1 text-xs text-red-600 hover:bg-red-50"
        >
          Delete
        </button>
      </div>
    </li>
  );
}

function groupByDay(threads: ThreadSummary[]): Array<[string, ThreadSummary[]]> {
  const groups = new Map<string, ThreadSummary[]>();
  const now = new Date();
  const today = startOfDay(now).getTime();
  const yesterday = today - 24 * 60 * 60 * 1000;
  const sevenDaysAgo = today - 7 * 24 * 60 * 60 * 1000;
  const thirtyDaysAgo = today - 30 * 24 * 60 * 60 * 1000;

  for (const t of threads) {
    const updated = new Date(t.updatedAt).getTime();
    let label: string;
    if (updated >= today) label = 'Today';
    else if (updated >= yesterday) label = 'Yesterday';
    else if (updated >= sevenDaysAgo) label = 'Previous 7 days';
    else if (updated >= thirtyDaysAgo) label = 'Previous 30 days';
    else label = 'Older';
    const list = groups.get(label) ?? [];
    list.push(t);
    groups.set(label, list);
  }
  const order = ['Today', 'Yesterday', 'Previous 7 days', 'Previous 30 days', 'Older'];
  return order
    .filter((k) => groups.has(k))
    .map((k) => [k, groups.get(k)!] as [string, ThreadSummary[]]);
}

function startOfDay(d: Date): Date {
  const x = new Date(d);
  x.setHours(0, 0, 0, 0);
  return x;
}

function slug(s: string): string {
  return s.toLowerCase().replace(/[^a-z0-9]+/g, '-');
}
