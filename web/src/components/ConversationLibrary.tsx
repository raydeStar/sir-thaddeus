import { Link, useNavigate } from '@tanstack/react-router';
import { useEffect, useMemo, useState } from 'react';
import { MessageSquare, Pencil, Pin, PinOff, Plus, Search, Trash2 } from 'lucide-react';
import type { ThreadSummary } from '@thaddeus/shared-types';
import { PageScaffold } from './PageScaffold';
import { useChatStore } from '../stores/chatStore';

export function ConversationLibrary() {
  const navigate = useNavigate();
  const threads = useChatStore((state) => state.threads);
  const loading = useChatStore((state) => state.loading);
  const error = useChatStore((state) => state.error);
  const loadThreads = useChatStore((state) => state.loadThreads);
  const newThread = useChatStore((state) => state.newThread);
  const updateThread = useChatStore((state) => state.updateThread);
  const deleteThread = useChatStore((state) => state.deleteThread);
  const [query, setQuery] = useState('');
  const [busyThreadId, setBusyThreadId] = useState<string | null>(null);

  useEffect(() => {
    void loadThreads();
  }, [loadThreads]);

  const filtered = useMemo(() => {
    const normalized = query.trim().toLocaleLowerCase();
    if (!normalized) return threads;
    return threads.filter((thread) =>
      `${thread.title} ${thread.lastMessagePreview ?? ''}`
        .toLocaleLowerCase()
        .includes(normalized),
    );
  }, [query, threads]);

  const pinned = filtered.filter((thread) => thread.pinned);
  const grouped = groupByRecency(filtered.filter((thread) => !thread.pinned));

  const createConversation = async () => {
    try {
      const thread = await newThread();
      await navigate({
        to: '/chat/$threadId',
        params: { threadId: thread.id },
        search: { focusMessageId: undefined },
      });
    } catch {
      // The shared store exposes the runtime error in the page alert.
    }
  };

  const runMutation = async (threadId: string, mutation: () => Promise<unknown>) => {
    setBusyThreadId(threadId);
    try {
      await mutation();
    } catch {
      // The shared store exposes the runtime error in the page alert.
    } finally {
      setBusyThreadId(null);
    }
  };

  const rename = async (thread: ThreadSummary) => {
    const title = window.prompt('Rename conversation', thread.title);
    if (title === null || title.trim() === thread.title) return;
    await runMutation(thread.id, () => updateThread(thread.id, { title }));
  };

  const togglePin = async (thread: ThreadSummary) => {
    await runMutation(thread.id, () => updateThread(thread.id, { pinned: !thread.pinned }));
  };

  const remove = async (thread: ThreadSummary) => {
    if (!window.confirm(`Delete "${thread.title}"? This cannot be undone.`)) return;
    await runMutation(thread.id, () => deleteThread(thread.id));
  };

  return (
    <PageScaffold
      testId="route-chat"
      title="Conversations"
      subtitle="Find, resume, and manage your work with Sir Thaddeus."
    >
      <div className="mb-8 flex flex-col gap-4 sm:flex-row sm:items-center">
        <label className="relative min-w-0 flex-1">
          <span className="sr-only">Search conversations</span>
          <Search
            className="pointer-events-none absolute left-3.5 top-1/2 h-4 w-4 -translate-y-1/2 text-ink-subtle"
            strokeWidth={1.75}
            aria-hidden
          />
          <input
            type="search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Search titles and recent messages…"
            data-testid="conversations-search"
            className="block min-h-11 w-full rounded-full border border-line bg-canvas-raised py-2.5 pl-10 pr-4 text-sm text-ink placeholder:text-ink-subtle focus:border-accent-ring focus:outline-none focus:ring-2 focus:ring-accent/20"
          />
        </label>
        <button
          type="button"
          onClick={() => { void createConversation(); }}
          data-testid="chat-new-thread"
          className="btn-primary min-h-11 shrink-0"
        >
          <Plus className="h-4 w-4" strokeWidth={2} />
          New conversation
        </button>
      </div>

      <div className="mb-7 flex items-center justify-between text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
        <span data-testid="chat-thread-count">
          {threads.length} {threads.length === 1 ? 'conversation' : 'conversations'}
        </span>
        {query.trim() ? <span>{filtered.length} matching</span> : null}
      </div>

      {loading ? <p className="text-sm text-ink-muted">Loading…</p> : null}
      {error ? (
        <p
          role="alert"
          className="mb-5 rounded-xl border border-rose-500/25 bg-rose-500/10 px-4 py-3 text-sm text-rose-600 dark:text-rose-300"
          data-testid="chat-error"
        >
          {error}
        </p>
      ) : null}

      {!loading && filtered.length === 0 ? (
        <div data-testid="chat-empty" className="flex flex-col items-center gap-3 py-16 text-center">
          <span className="flex h-11 w-11 items-center justify-center rounded-2xl border border-line bg-canvas-raised text-ink-subtle">
            {query.trim()
              ? <Search className="h-5 w-5" strokeWidth={1.7} />
              : <MessageSquare className="h-5 w-5" strokeWidth={1.7} />}
          </span>
          <p className="text-base font-medium text-ink">
            {query.trim() ? 'No conversations match' : 'No conversations yet'}
          </p>
          <p className="max-w-sm text-sm text-ink-muted">
            {query.trim()
              ? 'Try a title or phrase from the latest message.'
              : 'Start one and Sir Thaddeus will keep it here for next time.'}
          </p>
          {!query.trim() ? (
            <button
              type="button"
              onClick={() => { void createConversation(); }}
              data-testid="chat-new-thread-empty"
              className="btn-primary mt-3"
            >
              <Plus className="h-4 w-4" strokeWidth={2} />
              New conversation
            </button>
          ) : null}
        </div>
      ) : null}

      {pinned.length > 0 ? (
        <ConversationSection title="Pinned" testId="conversations-pinned">
          {pinned.map((thread) => (
            <ConversationRow
              key={thread.id}
              thread={thread}
              busy={busyThreadId === thread.id}
              onPinToggle={togglePin}
              onRename={rename}
              onDelete={remove}
            />
          ))}
        </ConversationSection>
      ) : null}

      {grouped.map(([label, items]) => (
        <ConversationSection
          key={label}
          title={label}
          testId={`conversations-group-${slug(label)}`}
        >
          {items.map((thread) => (
            <ConversationRow
              key={thread.id}
              thread={thread}
              busy={busyThreadId === thread.id}
              onPinToggle={togglePin}
              onRename={rename}
              onDelete={remove}
            />
          ))}
        </ConversationSection>
      ))}
    </PageScaffold>
  );
}

function ConversationSection({
  title,
  testId,
  children,
}: {
  title: string;
  testId: string;
  children: React.ReactNode;
}) {
  return (
    <section data-testid={testId} className="mb-9">
      <h2 className="mb-2 px-1 text-[10px] font-semibold uppercase tracking-[0.12em] text-ink-subtle">
        {title}
      </h2>
      <ul className="divide-y divide-line rounded-2xl border border-line bg-canvas-raised px-3 sm:px-4">
        {children}
      </ul>
    </section>
  );
}

function ConversationRow({
  thread,
  busy,
  onPinToggle,
  onRename,
  onDelete,
}: {
  thread: ThreadSummary;
  busy: boolean;
  onPinToggle: (thread: ThreadSummary) => void | Promise<void>;
  onRename: (thread: ThreadSummary) => void | Promise<void>;
  onDelete: (thread: ThreadSummary) => void | Promise<void>;
}) {
  return (
    <li
      data-testid={`conversation-thread-${thread.id}`}
      className="group min-w-0 py-3.5 sm:flex sm:items-center sm:gap-3"
    >
      <Link
        to="/chat/$threadId"
        params={{ threadId: thread.id }}
        search={{ focusMessageId: undefined }}
        data-testid={`chat-thread-${thread.id}`}
        className="flex min-w-0 flex-1 items-start gap-3 rounded-xl p-1 outline-none transition focus-visible:ring-2 focus-visible:ring-accent/40"
      >
        <span className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-xl bg-canvas-sunken text-ink-subtle transition group-hover:text-accent">
          {thread.pinned
            ? <Pin className="h-3.5 w-3.5" strokeWidth={1.9} aria-hidden />
            : <MessageSquare className="h-3.5 w-3.5" strokeWidth={1.8} aria-hidden />}
        </span>
        <span className="min-w-0 flex-1">
          <span className="flex min-w-0 items-baseline justify-between gap-3">
            <span className="truncate text-[14px] font-medium text-ink group-hover:text-accent">
              {thread.title || 'Untitled conversation'}
            </span>
            <time className="shrink-0 text-[10px] tabular-nums text-ink-subtle" dateTime={thread.updatedAt}>
              {formatTimestamp(thread.updatedAt)}
            </time>
          </span>
          <span className="mt-0.5 block truncate text-[12px] text-ink-muted">
            {thread.lastMessagePreview || (thread.messageCount > 0
              ? `${thread.messageCount} messages`
              : 'Empty conversation')}
          </span>
        </span>
      </Link>

      <div className="mt-2 flex shrink-0 items-center justify-end gap-1 pl-12 opacity-100 transition sm:mt-0 sm:pl-0 sm:opacity-0 sm:group-hover:opacity-100 sm:group-focus-within:opacity-100">
        <RowAction
          label={thread.pinned ? `Unpin ${thread.title}` : `Pin ${thread.title}`}
          disabled={busy}
          onClick={() => { void onPinToggle(thread); }}
          icon={thread.pinned ? PinOff : Pin}
        />
        <RowAction
          label={`Rename ${thread.title}`}
          disabled={busy}
          onClick={() => { void onRename(thread); }}
          icon={Pencil}
        />
        <RowAction
          label={`Delete ${thread.title}`}
          disabled={busy}
          onClick={() => { void onDelete(thread); }}
          icon={Trash2}
          danger
        />
      </div>
    </li>
  );
}

function RowAction({
  label,
  disabled,
  onClick,
  icon: Icon,
  danger = false,
}: {
  label: string;
  disabled: boolean;
  onClick: () => void;
  icon: typeof Pin;
  danger?: boolean;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      disabled={disabled}
      onClick={onClick}
      className={`flex h-9 w-9 items-center justify-center rounded-full outline-none transition focus-visible:ring-2 focus-visible:ring-accent/50 disabled:opacity-40 ${
        danger
          ? 'text-ink-subtle hover:bg-rose-500/10 hover:text-rose-500'
          : 'text-ink-subtle hover:bg-accent-soft hover:text-accent'
      }`}
    >
      <Icon className="h-3.5 w-3.5" strokeWidth={1.8} aria-hidden />
    </button>
  );
}

export function groupByRecency(threads: ThreadSummary[], now = new Date()): Array<[string, ThreadSummary[]]> {
  const groups = new Map<string, ThreadSummary[]>();
  const today = startOfDay(now).getTime();
  const yesterday = today - 24 * 60 * 60 * 1000;
  const sevenDaysAgo = today - 7 * 24 * 60 * 60 * 1000;
  const thirtyDaysAgo = today - 30 * 24 * 60 * 60 * 1000;

  for (const thread of threads) {
    const updated = new Date(thread.updatedAt).getTime();
    const label = updated >= today
      ? 'Today'
      : updated >= yesterday
        ? 'Yesterday'
        : updated >= sevenDaysAgo
          ? 'Previous 7 days'
          : updated >= thirtyDaysAgo
            ? 'Previous 30 days'
            : 'Older';
    const group = groups.get(label) ?? [];
    group.push(thread);
    groups.set(label, group);
  }

  return ['Today', 'Yesterday', 'Previous 7 days', 'Previous 30 days', 'Older']
    .filter((label) => groups.has(label))
    .map((label) => [label, groups.get(label)!]);
}

function startOfDay(value: Date): Date {
  const result = new Date(value);
  result.setHours(0, 0, 0, 0);
  return result;
}

function formatTimestamp(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  const elapsed = Date.now() - date.getTime();
  if (elapsed >= 0 && elapsed < 60_000) return 'now';
  if (elapsed >= 0 && elapsed < 60 * 60_000) return `${Math.max(1, Math.round(elapsed / 60_000))}m`;
  if (elapsed >= 0 && elapsed < 24 * 60 * 60_000) return `${Math.round(elapsed / (60 * 60_000))}h`;
  if (elapsed >= 0 && elapsed < 7 * 24 * 60 * 60_000) return `${Math.round(elapsed / (24 * 60 * 60_000))}d`;
  return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

function slug(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, '-');
}
