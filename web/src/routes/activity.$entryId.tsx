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
      <div className="mb-4 text-sm">
        <Link to="/activity" className="text-thaddeus-ink underline">
          ← Back to activity
        </Link>
      </div>

      {error ? (
        <p className="text-sm text-red-600" data-testid="activity-entry-error">
          {error}
        </p>
      ) : !entry ? (
        <p className="text-sm italic text-slate-500" data-testid="activity-entry-loading">
          Loading…
        </p>
      ) : (
        <dl
          data-testid="activity-entry-detail"
          className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-2 text-sm"
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
              <dt className="font-medium text-slate-700">Thread</dt>
              <dd>
                <Link
                  to="/chat/$threadId"
                  params={{ threadId: entry.threadId }}
                  className="text-thaddeus-ink underline"
                  data-testid="activity-entry-thread-link"
                >
                  {entry.threadId}
                </Link>
              </dd>
            </>
          ) : null}
          {entry.detail ? (
            <>
              <dt className="font-medium text-slate-700">Detail</dt>
              <dd
                data-testid="activity-entry-detail-text"
                className="whitespace-pre-wrap text-slate-600"
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
      <dt className="font-medium text-slate-700">{label}</dt>
      <dd data-testid={testId} className="text-slate-600">
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
