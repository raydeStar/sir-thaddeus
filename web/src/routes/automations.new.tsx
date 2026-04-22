import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { Sparkles, Loader2, Wand2 } from 'lucide-react';
import { PageScaffold } from '../components/PageScaffold';
import { ToolPicker } from '../components/ToolPicker';
import { SchedulePicker } from '../components/SchedulePicker';
import { createAutomation, draftAutomation, suggestTools } from '../lib/automationsApi';
import type { AutomationSchedule } from '@thaddeus/shared-types';

export const Route = createFileRoute('/automations/new')({
  component: NewAutomationRoute,
});

const inputCls =
  'block w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 text-sm text-ink placeholder:text-ink-subtle focus:border-accent-ring focus:outline-none focus:ring-2 focus:ring-accent/20';

function NewAutomationRoute() {
  const navigate = useNavigate();

  const [goal, setGoal] = useState('');
  const [drafting, setDrafting] = useState(false);
  const [draftNote, setDraftNote] = useState<string | null>(null);

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [stepsText, setStepsText] = useState('');
  const [enabled, setEnabled] = useState(true);
  const [allowedTools, setAllowedTools] = useState<string[]>([]);
  const [schedule, setSchedule] = useState<AutomationSchedule | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const parsedSteps = useMemo(
    () => stepsText.split('\n').map((s) => s.trim()).filter(Boolean),
    [stepsText],
  );

  /**
   * Goal → draft → plan flow:
   *   1. Send the one-sentence goal to /api/automations/draft (LLM function
   *      call returns name/description/steps).
   *   2. If steps came back, immediately run /api/automations/suggest-tools
   *      as a "dry run" — the LLM walks each step and reports which tools
   *      it would call. That list becomes the initial allowlist.
   *   3. User sees the whole form filled in, can edit anything.
   */
  const onDraft = async () => {
    if (drafting) return;
    const trimmed = goal.trim();
    if (!trimmed) {
      setDraftNote('Type a goal first — e.g. "Check walmart.com for PS5 availability".');
      return;
    }
    setDrafting(true);
    setDraftNote(null);
    try {
      const result = await draftAutomation(trimmed);
      if (result.steps.length === 0) {
        setDraftNote(result.note ?? 'The model did not return any steps. Try rephrasing the goal.');
        return;
      }
      setName(result.name ?? trimmed.slice(0, 60));
      setDescription(result.description ?? '');
      setStepsText(result.steps.join('\n'));

      // Dry-run stage: ask the model which tools it would invoke for these steps.
      try {
        const plan = await suggestTools({
          name: result.name ?? trimmed,
          description: result.description ?? '',
          steps: result.steps,
        });
        setAllowedTools(plan.tools);
        setDraftNote(
          plan.tools.length > 0
            ? `Drafted ${result.steps.length} step${result.steps.length === 1 ? '' : 's'} · dry run picked ${plan.tools.length} tool${plan.tools.length === 1 ? '' : 's'}.`
            : `Drafted ${result.steps.length} step${result.steps.length === 1 ? '' : 's'}. Dry run picked no tools; pick them below if needed.`,
        );
      } catch (e) {
        setDraftNote(`Drafted steps, but dry run failed: ${(e as Error).message}`);
      }
    } catch (e) {
      setDraftNote((e as Error).message);
    } finally {
      setDrafting(false);
    }
  };

  const onSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy || !name.trim()) return;
    setBusy(true);
    setError(null);
    try {
      const created = await createAutomation({
        name: name.trim(),
        description,
        steps: parsedSteps,
        enabled,
        allowedTools,
        schedule: schedule ?? undefined,
      });
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
      subtitle="Describe what you want done in a sentence, or fill it in manually."
    >
      <form onSubmit={onSubmit} data-testid="automation-new-form" className="space-y-6">
        {/* Goal-to-draft. The short sentence becomes the starting point for
            the whole form — user can edit anything afterwards. */}
        <section className="rounded-2xl border border-line bg-canvas-raised p-4">
          <label className="block">
            <span className="flex items-center gap-1.5 text-[12px] font-medium text-ink-muted">
              <Wand2 className="h-3.5 w-3.5 text-accent" strokeWidth={1.75} />
              Describe your goal
            </span>
            <div className="mt-2 flex items-start gap-2">
              <input
                type="text"
                data-testid="automation-new-goal"
                placeholder='e.g. "Check walmart.com for PS5 availability"'
                value={goal}
                onChange={(e) => {
                  setGoal(e.target.value);
                  if (draftNote) setDraftNote(null);
                }}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    void onDraft();
                  }
                }}
                className={inputCls}
              />
              <button
                type="button"
                data-testid="automation-new-draft"
                onClick={() => void onDraft()}
                disabled={drafting}
                className="inline-flex shrink-0 items-center gap-1.5 rounded-full bg-accent px-4 py-2 text-sm font-medium text-white transition hover:opacity-90 disabled:opacity-50"
              >
                {drafting ? (
                  <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.75} />
                ) : (
                  <Sparkles className="h-4 w-4" strokeWidth={1.75} />
                )}
                Draft
              </button>
            </div>
            {draftNote ? (
              <p className="mt-2 text-[12px] text-ink-muted" data-testid="automation-new-draft-note">
                {draftNote}
              </p>
            ) : (
              <p className="mt-2 text-[12px] text-ink-subtle">
                Sir Thaddeus will propose a name, steps, and a minimal tool list. Edit anything below.
              </p>
            )}
          </label>
        </section>

        <div className="space-y-4">
          <input
            type="text"
            data-testid="automation-new-name"
            placeholder="Name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            className={inputCls}
          />
          <textarea
            data-testid="automation-new-description"
            placeholder="Description (optional)"
            rows={2}
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            className={inputCls}
          />
          <textarea
            data-testid="automation-new-steps"
            placeholder="One step per line — e.g. 'Check the weather in Olympia, WA'"
            rows={6}
            value={stepsText}
            onChange={(e) => setStepsText(e.target.value)}
            className={`${inputCls} font-mono`}
          />
        </div>

        <ToolPicker
          value={allowedTools}
          onChange={setAllowedTools}
          automationName={name}
          automationDescription={description}
          steps={parsedSteps}
        />

        <SchedulePicker value={schedule} onChange={setSchedule} />

        <label className="flex items-center gap-2 text-sm text-ink">
          <input
            type="checkbox"
            data-testid="automation-new-enabled"
            checked={enabled}
            onChange={(e) => setEnabled(e.target.checked)}
            className="h-[14px] w-[14px] accent-accent"
          />
          Enabled
        </label>
        {error ? (
          <p data-testid="automation-new-error" className="text-sm text-rose-500">
            {error}
          </p>
        ) : null}
        <button
          type="submit"
          data-testid="automation-new-submit"
          disabled={busy || !name.trim()}
          className="inline-flex items-center gap-1.5 rounded-full bg-accent px-4 py-2 text-sm font-medium text-white transition hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-40"
        >
          {busy ? 'Creating…' : 'Create'}
        </button>
      </form>
    </PageScaffold>
  );
}
