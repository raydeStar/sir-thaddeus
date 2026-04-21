import { createFileRoute, Link } from '@tanstack/react-router';
import { useEffect } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { useActivityStore } from '../stores/activityStore';
import type { ActivityEntry, ActivityStatus } from '@thaddeus/shared-types';

export const Route = createFileRoute('/activity/')({
  component: ActivityRoute,
});

function ActivityRoute() {
  const entries = useActivityStore((s) => s.entries);
  const loading = useActivityStore((s) => s.loading);
  const error = useActivityStore((s) => s.error);
  const connect = useActivityStore((s) => s.connect);

  useEffect(() => {
    void connect();
  }, [connect]);

  return (
    <PageScaffold
      testId="route-activity"
      title="Activity"
      subtitle="Chat turns, voice turns, and automation runs from this runtime."
    >
      {error ? (
        <p className="mb-3 text-sm text-rose-500" data-testid="activity-error">
          {error}
        </p>
      ) : null}

      {loading && entries.length === 0 ? (
        <p className="text-sm text-ink-muted" data-testid="activity-loading">
          Loading…
        </p>
      ) : entries.length === 0 ? (
        <p className="text-sm text-ink-muted" data-testid="activity-empty">
          No activity yet. Send a message to populate the log.
        </p>
      ) : (
        <ul data-testid="activity-list" className="divide-y divide-line">
          {entries.map((entry) => (
            <ActivityRow key={entry.id} entry={entry} />
          ))}
        </ul>
      )}
    </PageScaffold>
  );
}

function ActivityRow({ entry }: { entry: ActivityEntry }) {
  return (
    <li
      data-testid={`activity-row-${entry.id}`}
      data-status={entry.status}
      data-kind={entry.kind}
      className="group py-3.5"
    >
      <Link
        to="/activity/$entryId"
        params={{ entryId: entry.id }}
        className="flex items-start justify-between gap-3 transition-colors hover:text-accent"
      >
        <div className="min-w-0 flex-1">
          <p className="truncate text-[15px] font-medium text-ink group-hover:text-accent">
            {entry.summary}
          </p>
          <p className="mt-0.5 text-xs text-ink-muted">
            <span data-testid={`activity-row-${entry.id}-kind`}>{entry.kind}</span>
            <span className="mx-1.5">·</span>
            <time dateTime={entry.startedAt}>{formatTime(entry.startedAt)}</time>
          </p>
        </div>
        <StatusBadge status={entry.status} />
      </Link>
    </li>
  );
}

function StatusBadge({ status }: { status: ActivityStatus }) {
  const { dot, label } = badgeTone(status);
  return (
    <span
      data-testid={`activity-status-${status}`}
      className="inline-flex shrink-0 items-center gap-1.5 rounded-full border border-line px-2.5 py-0.5 text-[11px] font-medium text-ink-muted"
    >
      <span aria-hidden className={`inline-block h-1.5 w-1.5 rounded-full ${dot}`} />
      {label}
    </span>
  );
}

function badgeTone(status: ActivityStatus): { dot: string; label: string } {
  switch (status) {
    case 'Running':
      return { dot: 'bg-amber-500', label: status };
    case 'Ok':
      return { dot: 'bg-emerald-500', label: status };
    case 'Failed':
      return { dot: 'bg-rose-500', label: status };
    case 'Cancelled':
      return { dot: 'bg-ink-subtle', label: status };
    default:
      return { dot: 'bg-ink-subtle', label: String(status) };
  }
}

function formatTime(iso: string): string {
  try {
    return new Date(iso).toLocaleTimeString();
  } catch {
    return iso;
  }
}
