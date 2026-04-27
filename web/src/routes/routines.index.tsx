import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { listRoutines, startRun } from '../lib/routinesApi';
import type { Routine } from '@thaddeus/shared-types';

export const Route = createFileRoute('/routines/')({
  component: RoutinesListRoute,
});

function RoutinesListRoute() {
  const navigate = useNavigate();
  const [items, setItems] = useState<Routine[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [runningId, setRunningId] = useState<string | null>(null);

  const load = async () => {
    try {
      setItems(await listRoutines());
    } catch (e) {
      setError((e as Error).message);
    }
  };
  useEffect(() => {
    void load();
  }, []);

  const onRun = async (id: string) => {
    setRunningId(id);
    setError(null);
    try {
      const run = await startRun(id);
      void navigate({ to: '/routines/$id/run', params: { id }, search: { runId: run.id } });
    } catch (e) {
      setError((e as Error).message);
      setRunningId(null);
    }
  };

  return (
    <PageScaffold
      testId="route-routines"
      title="Routines"
      subtitle="Checklists Sir Thaddeus walks you through — you stay in the driver's seat."
    >
      {error ? (
        <p data-testid="routines-error" className="mb-3 text-sm text-rose-500">
          {error}
        </p>
      ) : null}

      {items === null && !error ? (
        <p className="text-sm italic text-ink-subtle" data-testid="routines-loading">
          Loading…
        </p>
      ) : items !== null && items.length === 0 ? (
        <p className="text-sm text-ink-muted" data-testid="routines-empty">
          No routines yet.
        </p>
      ) : items !== null ? (
        <ul data-testid="routines-list" className="space-y-3">
          {items.map((r) => (
            <RoutineCard
              key={r.id}
              routine={r}
              running={runningId === r.id}
              onRun={() => void onRun(r.id)}
            />
          ))}
        </ul>
      ) : null}
    </PageScaffold>
  );
}

interface RoutineCardProps {
  routine: Routine;
  running: boolean;
  onRun: () => void;
}

function RoutineCard({ routine, running, onRun }: RoutineCardProps) {
  const itemCount = routine.checklistItems.length;
  const lastRun = routine.lastRunAt ? new Date(routine.lastRunAt).toLocaleString() : null;

  return (
    <li
      data-testid={`routine-item-${routine.id}`}
      className="surface flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:justify-between"
    >
      <div className="min-w-0">
        <p className="text-sm font-semibold text-ink">{routine.name}</p>
        {routine.description ? (
          <p className="mt-0.5 text-xs text-ink-muted">{routine.description}</p>
        ) : null}
        <p className="mt-1 text-[11px] uppercase tracking-wide text-ink-subtle">
          {itemCount} item{itemCount === 1 ? '' : 's'}
          {lastRun ? ` · last run ${lastRun}` : ' · never run'}
          {routine.enabled ? '' : ' · disabled'}
        </p>
      </div>

      <div className="flex shrink-0 items-center gap-2 text-xs">
        <button
          type="button"
          data-testid={`routine-run-${routine.id}`}
          disabled={!routine.enabled || running}
          onClick={onRun}
          className="rounded-full bg-accent px-4 py-1.5 font-medium text-white transition hover:opacity-90 disabled:opacity-50"
        >
          {running ? 'Starting…' : 'Run'}
        </button>
        <Link
          to="/routines/$id/edit"
          params={{ id: routine.id }}
          data-testid={`routine-edit-${routine.id}`}
          className="rounded-full border border-line px-3 py-1.5 text-ink-muted transition hover:text-ink"
        >
          Edit
        </Link>
        <Link
          to="/routines/$id/history"
          params={{ id: routine.id }}
          data-testid={`routine-history-${routine.id}`}
          className="rounded-full border border-line px-3 py-1.5 text-ink-muted transition hover:text-ink"
        >
          History
        </Link>
      </div>
    </li>
  );
}
