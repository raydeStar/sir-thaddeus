import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { Plus } from 'lucide-react';
import { PageScaffold } from '../components/PageScaffold';
import { createRoutine, listRoutines, startRun, updateRoutine } from '../lib/routinesApi';
import type { Routine } from '@thaddeus/shared-types';

export const Route = createFileRoute('/routines/')({
  component: RoutinesListRoute,
});

function RoutinesListRoute() {
  const navigate = useNavigate();
  const [items, setItems] = useState<Routine[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [runningId, setRunningId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [showDisabled, setShowDisabled] = useState(false);
  const [pendingToggleId, setPendingToggleId] = useState<string | null>(null);

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

  const onCreate = async () => {
    if (creating) return;
    setCreating(true);
    setError(null);
    try {
      const r = await createRoutine({
        name: 'New routine',
        description: '',
        checklistItems: [],
        promptTemplate: '',
        enabled: true,
      });
      void navigate({ to: '/routines/$id/edit', params: { id: r.id } });
    } catch (e) {
      setError((e as Error).message);
      setCreating(false);
    }
  };

  const onToggleEnabled = async (routine: Routine) => {
    if (pendingToggleId) return;
    setPendingToggleId(routine.id);
    setError(null);
    try {
      await updateRoutine(routine.id, { enabled: !routine.enabled });
      await load();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setPendingToggleId(null);
    }
  };

  const visibleItems = items
    ? showDisabled
      ? items
      : items.filter((r) => r.enabled)
    : null;
  const disabledCount = items ? items.filter((r) => !r.enabled).length : 0;

  return (
    <PageScaffold
      testId="route-routines"
      title="Routines"
      subtitle="Checklists Sir Thaddeus walks you through — you stay in the driver's seat."
    >
      <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <button
            type="button"
            data-testid="routines-new"
            disabled={creating}
            onClick={() => void onCreate()}
            className="btn-primary"
          >
            <Plus className="h-4 w-4" strokeWidth={2} />
            {creating ? 'Creating…' : 'New routine'}
          </button>
          {disabledCount > 0 ? (
            <button
              type="button"
              data-testid="routines-toggle-disabled"
              onClick={() => setShowDisabled((s) => !s)}
              className="text-xs text-ink-muted underline-offset-2 transition hover:text-ink hover:underline"
            >
              {showDisabled
                ? `Hide disabled (${disabledCount})`
                : `Show disabled (${disabledCount})`}
            </button>
          ) : null}
        </div>
      </div>

      {error ? (
        <p data-testid="routines-error" className="mb-3 text-sm text-rose-500">
          {error}
        </p>
      ) : null}

      {items === null && !error ? (
        <p className="text-sm italic text-ink-subtle" data-testid="routines-loading">
          Loading…
        </p>
      ) : visibleItems !== null && visibleItems.length === 0 ? (
        <p className="text-sm text-ink-muted" data-testid="routines-empty">
          {items && items.length > 0
            ? 'No enabled routines. Toggle one back on or create a new one.'
            : 'No routines yet. Click "New routine" to start one.'}
        </p>
      ) : visibleItems !== null ? (
        <ul data-testid="routines-list" className="space-y-3">
          {visibleItems.map((r) => (
            <RoutineCard
              key={r.id}
              routine={r}
              running={runningId === r.id}
              toggling={pendingToggleId === r.id}
              onRun={() => void onRun(r.id)}
              onToggleEnabled={() => void onToggleEnabled(r)}
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
  toggling: boolean;
  onRun: () => void;
  onToggleEnabled: () => void;
}

function RoutineCard({ routine, running, toggling, onRun, onToggleEnabled }: RoutineCardProps) {
  const itemCount = routine.checklistItems.length;
  const lastRun = routine.lastRunAt ? new Date(routine.lastRunAt).toLocaleString() : null;

  return (
    <li
      data-testid={`routine-item-${routine.id}`}
      data-enabled={routine.enabled}
      className={`surface flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:justify-between ${
        routine.enabled ? '' : 'opacity-70'
      }`}
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
          role="switch"
          aria-checked={routine.enabled}
          aria-label={routine.enabled ? 'Disable routine' : 'Enable routine'}
          data-testid={`routine-toggle-${routine.id}`}
          disabled={toggling}
          onClick={onToggleEnabled}
          className={`relative h-[22px] w-[38px] shrink-0 rounded-full transition-colors disabled:opacity-50 ${
            routine.enabled ? 'bg-accent' : 'bg-line-strong'
          }`}
        >
          <span
            className={`absolute top-0.5 left-0.5 h-[18px] w-[18px] rounded-full bg-white shadow-sm transition-transform ${
              routine.enabled ? 'translate-x-4' : 'translate-x-0'
            }`}
          />
        </button>
        <button
          type="button"
          data-testid={`routine-run-${routine.id}`}
          disabled={!routine.enabled || running}
          onClick={onRun}
          className="btn-primary py-1.5"
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
