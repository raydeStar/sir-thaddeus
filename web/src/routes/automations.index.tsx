import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import cronstrue from 'cronstrue';
import { PageScaffold } from '../components/PageScaffold';
import { listAutomations, runAutomation } from '../lib/automationsApi';
import type { Automation } from '@thaddeus/shared-types';

function scheduleSummary(a: Automation): string | null {
  const s = a.schedule;
  if (!s || s.kind === 'off') return null;
  if (s.kind === 'one-shot') {
    if (!s.runAt) return 'one-time · no date set';
    const d = new Date(s.runAt);
    return `once at ${d.toLocaleString()}`;
  }
  if (s.kind === 'cron' && s.cron) {
    try {
      return cronstrue.toString(s.cron, { use24HourTimeFormat: false });
    } catch {
      return `cron: ${s.cron}`;
    }
  }
  return null;
}

function formatNextRun(iso: string | null | undefined): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  const diffMs = d.getTime() - Date.now();
  const diffMin = Math.round(diffMs / 60_000);
  if (diffMin < 1) return `any moment · ${d.toLocaleTimeString()}`;
  if (diffMin < 60) return `in ${diffMin}m`;
  const diffHr = Math.round(diffMin / 60);
  if (diffHr < 24) return `in ${diffHr}h`;
  return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
}

export const Route = createFileRoute('/automations/')({
  component: AutomationsListRoute,
});

function AutomationsListRoute() {
  const navigate = useNavigate();
  const [items, setItems] = useState<Automation[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [runningId, setRunningId] = useState<string | null>(null);

  const load = async () => {
    try {
      setItems(await listAutomations());
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
      const result = await runAutomation(id);
      // Navigate to the run's thread so the user watches it stream live.
      void navigate({ to: '/chat/$threadId', params: { threadId: result.threadId } });
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setRunningId(null);
    }
  };

  return (
    <PageScaffold
      testId="route-automations"
      title="Automations"
      subtitle="Saved instructions Sir Thaddeus runs on demand."
    >
      <div className="mb-4">
        <Link
          to="/automations/new"
          data-testid="automation-new-link"
          className="inline-flex items-center gap-1.5 rounded-full bg-accent px-4 py-2 text-sm font-medium text-white transition hover:opacity-90"
        >
          New automation
        </Link>
      </div>

      {error ? (
        <p data-testid="automation-error" className="mb-3 text-sm text-rose-500">
          {error}
        </p>
      ) : null}

      {items === null ? (
        <p className="text-sm italic text-ink-subtle" data-testid="automation-loading">
          Loading…
        </p>
      ) : items.length === 0 ? (
        <p className="text-sm text-ink-muted" data-testid="automation-empty">
          No automations yet.
        </p>
      ) : (
        <ul data-testid="automation-list" className="space-y-2">
          {items.map((a) => (
            <li
              key={a.id}
              data-testid={`automation-item-${a.id}`}
              className="surface flex items-center justify-between p-4"
            >
              <div className="min-w-0">
                <Link
                  to="/automations/$id"
                  params={{ id: a.id }}
                  data-testid={`automation-link-${a.id}`}
                  className="text-sm font-semibold text-ink hover:underline"
                >
                  {a.name}
                </Link>
                <p className="mt-0.5 text-xs text-ink-muted">
                  {a.steps.length} step{a.steps.length === 1 ? '' : 's'}
                  {a.lastRunAt ? ` · last run ${new Date(a.lastRunAt).toLocaleString()}` : ' · never run'}
                  {a.enabled ? '' : ' · disabled'}
                </p>
                {(() => {
                  const summary = scheduleSummary(a);
                  const next = formatNextRun(a.schedule?.nextRunAt);
                  if (!summary) return null;
                  return (
                    <p className="mt-0.5 text-xs text-accent" data-testid={`automation-schedule-${a.id}`}>
                      {summary}{next ? ` · next ${next}` : ''}
                    </p>
                  );
                })()}
              </div>
              <button
                type="button"
                data-testid={`automation-run-${a.id}`}
                disabled={!a.enabled || runningId === a.id}
                onClick={() => void onRun(a.id)}
                className="rounded-full border border-line bg-canvas-raised px-3 py-1 text-xs text-ink-muted transition hover:bg-accent-soft hover:text-ink disabled:opacity-50"
              >
                {runningId === a.id ? 'Running…' : 'Run'}
              </button>
            </li>
          ))}
        </ul>
      )}
    </PageScaffold>
  );
}
