import { createFileRoute, Link } from '@tanstack/react-router';
import { useEffect, useMemo, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { getRoutine, listRuns } from '../lib/routinesApi';
import type { Routine, RoutineRun } from '@thaddeus/shared-types';

export const Route = createFileRoute('/routines/$id/history')({
  component: RoutineHistoryRoute,
});

function RoutineHistoryRoute() {
  const { id } = Route.useParams();
  const [routine, setRoutine] = useState<Routine | null | undefined>(undefined);
  const [runs, setRuns] = useState<RoutineRun[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [r, rs] = await Promise.all([getRoutine(id), listRuns(id)]);
        if (cancelled) return;
        setRoutine(r);
        setRuns(rs);
      } catch (e) {
        if (!cancelled) {
          setError((e as Error).message);
          setRoutine(null);
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [id]);

  if (routine === undefined) {
    return (
      <PageScaffold testId="route-routine-history" title="Loading…">
        <p className="text-sm italic text-ink-subtle">Loading…</p>
      </PageScaffold>
    );
  }
  if (routine === null) {
    return (
      <PageScaffold testId="route-routine-history" title="Not found">
        <p className="text-sm text-rose-500">{error ?? 'Routine not found.'}</p>
        <Link to="/routines" className="text-sm text-ink-muted underline hover:text-ink">
          Back to routines
        </Link>
      </PageScaffold>
    );
  }

  return (
    <PageScaffold
      testId="route-routine-history"
      title={`History: ${routine.name}`}
      subtitle="Every time you ran this routine, what you finished, and what you left."
    >
      {error ? (
        <p data-testid="routine-history-error" className="mb-3 text-sm text-rose-500">
          {error}
        </p>
      ) : null}

      {runs === null ? (
        <p className="text-sm italic text-ink-subtle">Loading…</p>
      ) : runs.length === 0 ? (
        <p className="text-sm text-ink-muted" data-testid="routine-history-empty">
          No runs yet.
        </p>
      ) : (
        <ul data-testid="routine-history-list" className="space-y-2">
          {runs.map((run) => (
            <HistoryRow key={run.id} run={run} />
          ))}
        </ul>
      )}

      <div className="mt-6">
        <Link
          to="/routines"
          className="text-sm text-ink-muted underline hover:text-ink"
        >
          Back to routines
        </Link>
      </div>
    </PageScaffold>
  );
}

function HistoryRow({ run }: { run: RoutineRun }) {
  const completionPercent = useMemo(() => {
    if (run.items.length === 0) return 0;
    const done = run.items.filter((i) => i.isCompleted).length;
    return Math.round((done / run.items.length) * 100);
  }, [run]);

  const started = new Date(run.startedAt).toLocaleString();
  const completed = run.completedAt ? new Date(run.completedAt).toLocaleString() : null;
  const notePreview = run.userNote
    ? run.userNote.length > 140
      ? `${run.userNote.slice(0, 140).trim()}…`
      : run.userNote
    : null;

  return (
    <li
      data-testid={`routine-history-item-${run.id}`}
      className="surface p-4"
    >
      <p className="text-sm font-medium text-ink">
        {completed ? completed : `Started ${started}`}
      </p>
      <p className="mt-0.5 text-xs text-ink-muted">
        {completionPercent}% complete
        {completed ? null : ' · in progress'}
        {` · ${run.items.filter((i) => i.isCompleted).length}/${run.items.length} items`}
      </p>
      {notePreview ? (
        <p className="mt-2 whitespace-pre-wrap text-sm text-ink">{notePreview}</p>
      ) : null}
      {run.generatedSummary ? (
        <p className="mt-2 rounded-lg bg-canvas-raised p-3 text-sm text-ink">
          {run.generatedSummary}
        </p>
      ) : null}
    </li>
  );
}
