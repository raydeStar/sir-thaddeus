import { createFileRoute, Link } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { getActivityEntry } from '../lib/activityApi';
import { useActivityStore } from '../stores/activityStore';
import type { ActivityEntry } from '@thaddeus/shared-types';

export const Route = createFileRoute('/activity/$entryId')({
  component: ActivityEntryRoute,
});

function ActivityEntryRoute() {
  const { entryId } = Route.useParams();
  const cached = useActivityStore((s) => s.entries.find((e) => e.id === entryId));
  const [entry, setEntry] = useState<ActivityEntry | null>(cached ?? null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (cached) {
      setEntry(cached);
      return;
    }
    let cancelled = false;
    getActivityEntry(entryId)
      .then((e) => {
        if (!cancelled) setEntry(e);
      })
      .catch((e: Error) => {
        if (!cancelled) setError(e.message);
      });
    return () => {
      cancelled = true;
    };
  }, [entryId, cached]);

  return (
    <PageScaffold
      testId="route-activity-entry"
      title={entry?.summary ?? `Activity ${entryId}`}
      subtitle={entry ? `${entry.kind} · ${entry.status}` : 'Loading…'}
    >
      <div className="mb-6 text-sm">
        <Link to="/activity" className="text-ink-muted hover:text-accent">
          ← Back to activity
        </Link>
      </div>

      {error ? (
        <p className="text-sm text-rose-500" data-testid="activity-entry-error">
          {error}
        </p>
      ) : !entry ? (
        <p className="text-sm text-ink-muted" data-testid="activity-entry-loading">
          Loading…
        </p>
      ) : (
        <dl
          data-testid="activity-entry-detail"
          className="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-3 text-sm"
        >
          <Field label="Id" value={entry.id} testId="activity-entry-id" />
          <Field label="Kind" value={entry.kind} testId="activity-entry-kind" />
          <Field label="Status" value={entry.status} testId="activity-entry-status" />
          <Field label="Started" value={fmt(entry.startedAt)} testId="activity-entry-started" />
          <Field
            label="Completed"
            value={entry.completedAt ? fmt(entry.completedAt) : '—'}
            testId="activity-entry-completed"
          />
          {entry.threadId ? (
            <>
              <dt className="text-ink-muted">Thread</dt>
              <dd>
                <Link
                  to="/chat/$threadId"
                  params={{ threadId: entry.threadId }}
                  search={{ focusMessageId: undefined }}
                  className="text-ink hover:text-accent"
                  data-testid="activity-entry-thread-link"
                >
                  {entry.threadId}
                </Link>
              </dd>
            </>
          ) : null}
          {entry.detail ? (
            <>
              <dt className="text-ink-muted">Detail</dt>
              <dd
                data-testid="activity-entry-detail-text"
                className="whitespace-pre-wrap text-ink"
              >
                {entry.detail}
              </dd>
            </>
          ) : null}
        </dl>
      )}
    </PageScaffold>
  );
}

function Field({ label, value, testId }: { label: string; value: string; testId: string }) {
  return (
    <>
      <dt className="text-ink-muted">{label}</dt>
      <dd data-testid={testId} className="text-ink">
        {value}
      </dd>
    </>
  );
}

function fmt(iso: string): string {
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}
