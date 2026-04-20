import { createFileRoute, Link } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { deleteAutomation, getAutomation, runAutomation } from '../lib/automationsApi';
import type { Automation } from '@thaddeus/shared-types';

export const Route = createFileRoute('/automations/$id')({
  component: AutomationRoute,
});

function AutomationRoute() {
  const { id } = Route.useParams();
  const [item, setItem] = useState<Automation | null | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = async () => {
    try {
      setItem(await getAutomation(id));
    } catch (e) {
      setError((e as Error).message);
      setItem(null);
    }
  };
  useEffect(() => {
    void load();
  }, [id]);

  const onRun = async () => {
    setBusy(true);
    setError(null);
    try {
      await runAutomation(id);
      await load();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const onDelete = async () => {
    setBusy(true);
    try {
      await deleteAutomation(id);
      window.location.assign('/automations');
    } catch (e) {
      setError((e as Error).message);
      setBusy(false);
    }
  };

  if (item === undefined) {
    return (
      <PageScaffold testId="route-automation-detail" title="Loading…">
        <p className="text-sm italic text-slate-500" data-testid="automation-detail-loading">
          Loading…
        </p>
      </PageScaffold>
    );
  }
  if (item === null) {
    return (
      <PageScaffold testId="route-automation-detail" title="Not found">
        <p className="text-sm text-red-600" data-testid="automation-detail-error">
          {error ?? 'Automation not found.'}
        </p>
        <Link to="/automations" className="text-sm underline">
          Back to automations
        </Link>
      </PageScaffold>
    );
  }

  return (
    <PageScaffold
      testId="route-automation-detail"
      title={item.name}
      subtitle={item.description || 'No description.'}
    >
      <div className="mb-3 text-xs text-slate-500" data-testid="automation-detail-meta">
        {item.steps.length} step{item.steps.length === 1 ? '' : 's'}
        {item.lastRunAt ? ` · last run ${new Date(item.lastRunAt).toLocaleString()}` : ' · never run'}
        {item.enabled ? '' : ' · disabled'}
      </div>
      <ol className="mb-4 list-decimal space-y-1 pl-5 text-sm text-slate-700" data-testid="automation-detail-steps">
        {item.steps.map((s, i) => (
          <li key={i}>{s}</li>
        ))}
      </ol>
      {error ? (
        <p data-testid="automation-detail-error" className="mb-3 text-sm text-red-600">
          {error}
        </p>
      ) : null}
      <div className="flex gap-2">
        <button
          type="button"
          data-testid="automation-detail-run"
          onClick={() => void onRun()}
          disabled={!item.enabled || busy}
          className="rounded-md bg-thaddeus-ink px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50"
        >
          {busy ? 'Running…' : 'Run now'}
        </button>
        <button
          type="button"
          data-testid="automation-detail-delete"
          onClick={() => void onDelete()}
          disabled={busy}
          className="rounded-md border border-red-300 px-3 py-1.5 text-sm text-red-700 disabled:opacity-50"
        >
          Delete
        </button>
        <Link to="/automations" className="self-center text-sm text-slate-500 underline">
          Back
        </Link>
      </div>
    </PageScaffold>
  );
}
