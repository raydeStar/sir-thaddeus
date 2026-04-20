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
        <p className="mb-3 text-sm text-red-600" data-testid="activity-error">
          {error}
        </p>
      ) : null}

      {loading && entries.length === 0 ? (
        <p className="text-sm italic text-slate-500" data-testid="activity-loading">
          Loading…
        </p>
      ) : entries.length === 0 ? (
        <p className="text-sm italic text-slate-500" data-testid="activity-empty">
          No activity yet. Send a message to populate the log.
        </p>
      ) : (
        <ul data-testid="activity-list" className="divide-y divide-slate-100">
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
      className="py-3"
    >
      <Link
        to="/activity/$entryId"
        params={{ entryId: entry.id }}
        className="flex items-start justify-between gap-3 hover:bg-slate-50"
      >
        <div className="min-w-0 flex-1">
          <p className="truncate text-sm font-medium text-thaddeus-ink">{entry.summary}</p>
          <p className="mt-0.5 text-xs text-slate-500">
            <span data-testid={`activity-row-${entry.id}-kind`}>{entry.kind}</span>
            {' · '}
            <time dateTime={entry.startedAt}>{formatTime(entry.startedAt)}</time>
          </p>
        </div>
        <StatusBadge status={entry.status} />
      </Link>
    </li>
  );
}

function StatusBadge({ status }: { status: ActivityStatus }) {
  const tone = badgeTone(status);
  return (
    <span
      data-testid={`activity-status-${status}`}
      className={`inline-flex shrink-0 items-center rounded-full px-2 py-0.5 text-xs font-medium ${tone}`}
    >
      {status}
    </span>
  );
}

function badgeTone(status: ActivityStatus): string {
  switch (status) {
    case 'Running':
      return 'bg-amber-100 text-amber-800';
    case 'Ok':
      return 'bg-emerald-100 text-emerald-800';
    case 'Failed':
      return 'bg-red-100 text-red-800';
    case 'Cancelled':
      return 'bg-slate-200 text-slate-700';
    default:
      return 'bg-slate-100 text-slate-700';
  }
}

function formatTime(iso: string): string {
  try {
    return new Date(iso).toLocaleTimeString();
  } catch {
    return iso;
  }
}
