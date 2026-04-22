import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { ToolPicker } from '../components/ToolPicker';
import { SchedulePicker } from '../components/SchedulePicker';
import {
  deleteAutomation,
  getAutomation,
  runAutomation,
  updateAutomation,
} from '../lib/automationsApi';
import type { Automation, AutomationSchedule } from '@thaddeus/shared-types';

export const Route = createFileRoute('/automations/$id')({
  component: AutomationRoute,
});

function AutomationRoute() {
  const { id } = Route.useParams();
  const navigate = useNavigate();
  const [item, setItem] = useState<Automation | null | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const [savingTools, setSavingTools] = useState(false);
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
      const result = await runAutomation(id);
      void navigate({ to: '/chat/$threadId', params: { threadId: result.threadId } });
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

  // Debounced allowlist save: update local state instantly, persist on change.
  const onAllowedToolsChange = async (next: string[]) => {
    if (!item) return;
    const prior = item;
    setItem({ ...item, allowedTools: next });
    setSavingTools(true);
    try {
      const updated = await updateAutomation(id, { allowedTools: next });
      setItem(updated);
    } catch (e) {
      setError((e as Error).message);
      setItem(prior);
    } finally {
      setSavingTools(false);
    }
  };

  const onScheduleChange = async (next: AutomationSchedule) => {
    if (!item) return;
    const prior = item;
    setItem({ ...item, schedule: next });
    try {
      const updated = await updateAutomation(id, { schedule: next });
      setItem(updated);
    } catch (e) {
      setError((e as Error).message);
      setItem(prior);
    }
  };

  if (item === undefined) {
    return (
      <PageScaffold testId="route-automation-detail" title="Loading…">
        <p className="text-sm italic text-ink-subtle" data-testid="automation-detail-loading">
          Loading…
        </p>
      </PageScaffold>
    );
  }
  if (item === null) {
    return (
      <PageScaffold testId="route-automation-detail" title="Not found">
        <p className="text-sm text-rose-500" data-testid="automation-detail-error">
          {error ?? 'Automation not found.'}
        </p>
        <Link to="/automations" className="text-sm text-ink-muted underline hover:text-ink">
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
      <div className="mb-3 text-xs text-ink-muted" data-testid="automation-detail-meta">
        {item.steps.length} step{item.steps.length === 1 ? '' : 's'}
        {item.lastRunAt ? ` · last run ${new Date(item.lastRunAt).toLocaleString()}` : ' · never run'}
        {item.enabled ? '' : ' · disabled'}
        {savingTools ? <span className="ml-2 text-accent">· saving…</span> : null}
      </div>
      <ol className="mb-6 list-decimal space-y-1 pl-5 text-sm text-ink" data-testid="automation-detail-steps">
        {item.steps.map((s, i) => (
          <li key={i}>{s}</li>
        ))}
      </ol>

      <div className="mb-6 border-t border-line pt-6">
        <ToolPicker
          value={item.allowedTools ?? []}
          onChange={onAllowedToolsChange}
          automationName={item.name}
          automationDescription={item.description}
          steps={item.steps}
        />
      </div>

      <div className="mb-6 border-t border-line pt-6">
        <SchedulePicker value={item.schedule} onChange={onScheduleChange} />
      </div>

      {error ? (
        <p data-testid="automation-detail-error" className="mb-3 text-sm text-rose-500">
          {error}
        </p>
      ) : null}
      <div className="flex items-center gap-2">
        <button
          type="button"
          data-testid="automation-detail-run"
          onClick={() => void onRun()}
          disabled={!item.enabled || busy}
          className="inline-flex items-center gap-1.5 rounded-full bg-accent px-4 py-2 text-sm font-medium text-white transition hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-40"
        >
          {busy ? 'Running…' : 'Run now'}
        </button>
        <button
          type="button"
          data-testid="automation-detail-delete"
          onClick={() => void onDelete()}
          disabled={busy}
          className="rounded-full border border-rose-500/30 px-3 py-1.5 text-sm text-rose-500 transition hover:bg-rose-500/10 disabled:opacity-50"
        >
          Delete
        </button>
        <Link to="/automations" className="text-sm text-ink-muted underline hover:text-ink">
          Back
        </Link>
      </div>
    </PageScaffold>
  );
}
