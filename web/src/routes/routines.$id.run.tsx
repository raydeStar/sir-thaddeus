import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useEffect, useMemo, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import {
  completeRun,
  discardRun,
  getRoutine,
  getRun,
  startRun,
  updateRun,
} from '../lib/routinesApi';
import type { Routine, RoutineRun } from '@thaddeus/shared-types';

export const Route = createFileRoute('/routines/$id/run')({
  component: RoutineRunRoute,
  validateSearch: (search: Record<string, unknown>) => ({
    runId: typeof search.runId === 'string' ? search.runId : undefined,
  }),
});

function RoutineRunRoute() {
  const { id } = Route.useParams();
  const { runId: searchRunId } = Route.useSearch();
  const navigate = useNavigate();

  const [routine, setRoutine] = useState<Routine | null>(null);
  const [run, setRun] = useState<RoutineRun | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [completing, setCompleting] = useState(false);
  const [note, setNote] = useState('');

  useEffect(() => {
    let cancelled = false;

    const load = async () => {
      try {
        const fetchedRoutine = await getRoutine(id);
        if (cancelled) return;
        setRoutine(fetchedRoutine);

        // If the caller passed a runId in the URL (the list view does this on
        // Run click), resume it. Otherwise mint a fresh run now. Keeping the
        // runId in the URL means refresh doesn't create a duplicate record.
        let activeRun: RoutineRun | null = null;
        if (searchRunId) {
          try {
            activeRun = await getRun(searchRunId);
          } catch {
            activeRun = null;
          }
        }
        if (!activeRun || activeRun.routineId !== id) {
          activeRun = await startRun(id);
          if (!cancelled) {
            void navigate({
              to: '/routines/$id/run',
              params: { id },
              search: { runId: activeRun.id },
              replace: true,
            });
          }
        }
        if (!cancelled) {
          setRun(activeRun);
          setNote(activeRun.userNote ?? '');
        }
      } catch (e) {
        if (!cancelled) setError((e as Error).message);
      }
    };

    void load();
    return () => {
      cancelled = true;
    };
  }, [id, searchRunId, navigate]);

  const completionPercent = useMemo(() => {
    if (!run || run.items.length === 0) return 0;
    const done = run.items.filter((i) => i.isCompleted).length;
    return Math.round((done / run.items.length) * 100);
  }, [run]);

  const sealed = run?.completedAt != null;

  const toggleItem = async (checklistItemId: string, nextCompleted: boolean) => {
    if (!run || sealed) return;
    const prior = run;
    setRun({
      ...run,
      items: run.items.map((i) =>
        i.checklistItemId === checklistItemId
          ? {
              ...i,
              isCompleted: nextCompleted,
              completedAt: nextCompleted ? new Date().toISOString() : null,
            }
          : i,
      ),
    });
    setSaving(true);
    try {
      const updated = await updateRun(run.id, {
        itemUpdates: [{ checklistItemId, isCompleted: nextCompleted }],
      });
      setRun(updated);
    } catch (e) {
      setError((e as Error).message);
      setRun(prior);
    } finally {
      setSaving(false);
    }
  };

  const saveNote = async () => {
    if (!run || sealed) return;
    if ((run.userNote ?? '') === note) return;
    setSaving(true);
    try {
      const updated = await updateRun(run.id, { userNote: note });
      setRun(updated);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSaving(false);
    }
  };

  const onComplete = async () => {
    if (!run || sealed) return;
    setCompleting(true);
    try {
      const updated = await completeRun(run.id, note);
      setRun(updated);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setCompleting(false);
    }
  };

  const onCancel = async () => {
    // Only discard if the run is truly empty — otherwise we'd delete the
    // user's already-logged checkmarks without asking. This matches the
    // brief's "avoid saving noise" guidance.
    if (run && !sealed) {
      const nothingDone =
        run.items.every((i) => !i.isCompleted) && (run.userNote ?? '') === '' && note === '';
      if (nothingDone) {
        try {
          await discardRun(run.id);
        } catch {
          /* best-effort */
        }
      }
    }
    void navigate({ to: '/routines' });
  };

  if (!routine || !run) {
    return (
      <PageScaffold testId="route-routine-run" title="Loading…">
        {error ? (
          <p className="text-sm text-rose-500" data-testid="routine-run-error">
            {error}
          </p>
        ) : (
          <p className="text-sm italic text-ink-subtle" data-testid="routine-run-loading">
            Loading…
          </p>
        )}
        <Link to="/routines" className="mt-4 inline-block text-sm text-ink-muted underline">
          Back to routines
        </Link>
      </PageScaffold>
    );
  }

  return (
    <PageScaffold
      testId="route-routine-run"
      title={routine.name}
      subtitle={routine.description || undefined}
    >
      <div
        className="mb-4 flex items-center gap-3 text-xs text-ink-muted"
        data-testid="routine-run-meta"
      >
        <span data-testid="routine-run-progress">
          {completionPercent}% complete
        </span>
        {saving ? <span className="text-accent">· saving…</span> : null}
        {sealed ? (
          <span className="rounded-full bg-accent-soft px-2 py-0.5 text-ink">Completed</span>
        ) : null}
      </div>

      <ul className="mb-6 space-y-1.5" data-testid="routine-run-items">
        {run.items.length === 0 ? (
          <li className="text-sm italic text-ink-subtle">
            This routine has no checklist items. Add some in the editor, or just leave a note.
          </li>
        ) : (
          run.items.map((item) => (
            <li
              key={item.checklistItemId}
              data-testid={`routine-run-item-${item.checklistItemId}`}
              className="flex items-start gap-3"
            >
              <input
                type="checkbox"
                data-testid={`routine-run-check-${item.checklistItemId}`}
                checked={item.isCompleted}
                disabled={sealed}
                onChange={(e) => void toggleItem(item.checklistItemId, e.target.checked)}
                className="mt-1 h-[15px] w-[15px] accent-accent"
              />
              <span
                className={`text-sm ${
                  item.isCompleted ? 'text-ink-muted line-through' : 'text-ink'
                }`}
              >
                {item.text}
              </span>
            </li>
          ))
        )}
      </ul>

      {routine.promptTemplate ? (
        <details className="mb-6 rounded-xl border border-line bg-canvas-raised p-3 text-xs text-ink-muted">
          <summary className="cursor-pointer select-none text-ink">
            Prompt template
          </summary>
          <p className="mt-2 whitespace-pre-wrap font-mono text-[11px] text-ink">
            {routine.promptTemplate}
          </p>
          <p className="mt-2 text-[11px] text-ink-subtle">
            Copy this into a chat when you want Sir Thaddeus to help with the routine.
          </p>
        </details>
      ) : null}

      <div className="mb-6">
        <label className="block text-[12px] font-medium text-ink-muted">
          Note
          <textarea
            data-testid="routine-run-note"
            rows={4}
            value={note}
            onChange={(e) => setNote(e.target.value)}
            onBlur={() => void saveNote()}
            disabled={sealed}
            placeholder="What mattered today? What's the next move?"
            className="mt-1.5 block w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 text-sm text-ink placeholder:text-ink-subtle focus:border-accent-ring focus:outline-none focus:ring-2 focus:ring-accent/20 disabled:opacity-60"
          />
        </label>
      </div>

      {error ? (
        <p data-testid="routine-run-error" className="mb-3 text-sm text-rose-500">
          {error}
        </p>
      ) : null}

      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          data-testid="routine-run-complete"
          disabled={sealed || completing}
          onClick={() => void onComplete()}
          className="btn-primary"
        >
          {completing ? 'Completing…' : sealed ? 'Completed' : 'Complete routine'}
        </button>
        <button
          type="button"
          data-testid="routine-run-cancel"
          onClick={() => void onCancel()}
          className="rounded-full border border-line px-3 py-1.5 text-sm text-ink-muted transition hover:text-ink"
        >
          {sealed ? 'Close' : 'Cancel'}
        </button>
        <Link
          to="/routines/$id/history"
          params={{ id }}
          className="ml-auto text-xs text-ink-muted underline hover:text-ink"
        >
          History
        </Link>
      </div>
    </PageScaffold>
  );
}
