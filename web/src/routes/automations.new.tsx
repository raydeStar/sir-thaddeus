import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { createAutomation } from '../lib/automationsApi';

export const Route = createFileRoute('/automations/new')({
  component: NewAutomationRoute,
});

function NewAutomationRoute() {
  const navigate = useNavigate();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [stepsText, setStepsText] = useState('');
  const [enabled, setEnabled] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const onSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy || !name.trim()) return;
    setBusy(true);
    setError(null);
    try {
      const steps = stepsText
        .split('\n')
        .map((s) => s.trim())
        .filter(Boolean);
      const created = await createAutomation({ name: name.trim(), description, steps, enabled });
      void navigate({ to: '/automations/$id', params: { id: created.id } });
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  };

  return (
    <PageScaffold
      testId="route-automation-new"
      title="New automation"
      subtitle="Describe a routine task. Steps run in order when triggered."
    >
      <form onSubmit={onSubmit} data-testid="automation-new-form" className="space-y-3">
        <input
          type="text"
          data-testid="automation-new-name"
          placeholder="Name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          className="w-full rounded-md border border-slate-300 px-3 py-1.5 text-sm"
        />
        <textarea
          data-testid="automation-new-description"
          placeholder="Description (optional)"
          rows={2}
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          className="w-full rounded-md border border-slate-300 px-3 py-1.5 text-sm"
        />
        <textarea
          data-testid="automation-new-steps"
          placeholder="One step per line"
          rows={6}
          value={stepsText}
          onChange={(e) => setStepsText(e.target.value)}
          className="w-full rounded-md border border-slate-300 px-3 py-1.5 text-sm font-mono"
        />
        <label className="flex items-center gap-2 text-sm text-slate-700">
          <input
            type="checkbox"
            data-testid="automation-new-enabled"
            checked={enabled}
            onChange={(e) => setEnabled(e.target.checked)}
          />
          Enabled
        </label>
        {error ? (
          <p data-testid="automation-new-error" className="text-sm text-red-600">
            {error}
          </p>
        ) : null}
        <button
          type="submit"
          data-testid="automation-new-submit"
          disabled={busy || !name.trim()}
          className="rounded-md bg-thaddeus-ink px-4 py-1.5 text-sm font-medium text-white disabled:opacity-50"
        >
          {busy ? 'Creating…' : 'Create'}
        </button>
      </form>
    </PageScaffold>
  );
}
