import { createFileRoute, Link } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { listAutomations, runAutomation } from '../lib/automationsApi';
import type { Automation } from '@thaddeus/shared-types';

export const Route = createFileRoute('/automations/')({
  component: AutomationsListRoute,
});

function AutomationsListRoute() {
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
      await runAutomation(id);
      await load();
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
          className="rounded-md bg-thaddeus-ink px-3 py-1.5 text-sm font-medium text-white"
        >
          New automation
        </Link>
      </div>

      {error ? (
        <p data-testid="automation-error" className="mb-3 text-sm text-red-600">
          {error}
        </p>
      ) : null}

      {items === null ? (
        <p className="text-sm italic text-slate-500" data-testid="automation-loading">
          Loading…
        </p>
      ) : items.length === 0 ? (
        <p className="text-sm text-slate-500" data-testid="automation-empty">
          No automations yet.
        </p>
      ) : (
        <ul data-testid="automation-list" className="space-y-2">
          {items.map((a) => (
            <li
              key={a.id}
              data-testid={`automation-item-${a.id}`}
              className="flex items-center justify-between rounded-md border border-slate-200 p-3"
            >
              <div>
                <Link
                  to="/automations/$id"
                  params={{ id: a.id }}
                  data-testid={`automation-link-${a.id}`}
                  className="text-sm font-semibold text-thaddeus-ink hover:underline"
                >
                  {a.name}
                </Link>
                <p className="text-xs text-slate-500">
                  {a.steps.length} step{a.steps.length === 1 ? '' : 's'}
                  {a.lastRunAt ? ` · last run ${new Date(a.lastRunAt).toLocaleString()}` : ' · never run'}
                  {a.enabled ? '' : ' · disabled'}
                </p>
              </div>
              <button
                type="button"
                data-testid={`automation-run-${a.id}`}
                disabled={!a.enabled || runningId === a.id}
                onClick={() => void onRun(a.id)}
                className="rounded-md border border-slate-300 px-3 py-1 text-xs disabled:opacity-50"
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
