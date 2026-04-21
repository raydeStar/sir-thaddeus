import { useMemo, useState } from 'react';
import { Calendar, Check, Loader2, Plus, Sparkles, X } from 'lucide-react';
import type { AutomationSchedule } from '@thaddeus/shared-types';
import { createAutomation } from '../lib/automationsApi';
import { useProposalsStore } from '../stores/proposalsStore';

interface ProposalCardProps {
  messageId: string;
}

/**
 * Inline editable confirmation card rendered inside the chat thread when
 * the assistant calls the virtual <code>propose_automation</code> tool.
 * The user can edit the name, steps, and schedule before pressing Create
 * (POSTs to <code>/api/automations</code>) or Cancel (dismisses the card).
 *
 * Phase C of the automations roadmap.
 */
export function ProposalCard({ messageId }: ProposalCardProps) {
  const state = useProposalsStore((s) => s.byMessage[messageId]);
  const setStatus = useProposalsStore((s) => s.setStatus);

  // Local edits. Seeded from the proposal once; re-seeding on every
  // render would stomp the user's typing.
  const initial = state?.proposal;
  const [name, setName] = useState(initial?.name ?? '');
  const [description, setDescription] = useState(initial?.description ?? '');
  const [stepsText, setStepsText] = useState((initial?.steps ?? []).join('\n'));
  const [schedule, setSchedule] = useState<AutomationSchedule | null>(
    initial?.schedule ?? null,
  );
  const [seeded, setSeeded] = useState(false);

  // One-shot seed when the proposal arrives after the component mounts.
  if (!seeded && initial) {
    setName(initial.name);
    setDescription(initial.description ?? '');
    setStepsText(initial.steps.join('\n'));
    setSchedule(initial.schedule ?? null);
    setSeeded(true);
  }

  const steps = useMemo(
    () => stepsText.split('\n').map((s) => s.trim()).filter(Boolean),
    [stepsText],
  );

  if (!state) return null;

  const isTerminal =
    state.status === 'created' ||
    state.status === 'cancelled';

  const onCreate = async () => {
    if (!name.trim() || steps.length === 0) return;
    setStatus(messageId, { status: 'creating', error: undefined });
    try {
      const created = await createAutomation({
        name: name.trim(),
        description: description.trim() || undefined,
        steps,
        enabled: true,
        schedule: schedule ?? undefined,
      });
      setStatus(messageId, { status: 'created', automationId: created.id });
    } catch (e) {
      setStatus(messageId, {
        status: 'error',
        error: (e as Error).message,
      });
    }
  };

  const onCancel = () => {
    setStatus(messageId, { status: 'cancelled' });
  };

  const scheduleLabel = describeSchedule(schedule);

  if (state.status === 'created') {
    return (
      <div
        data-testid={`proposal-card-${messageId}`}
        data-status="created"
        className="mb-3 rounded-2xl border border-emerald-500/30 bg-emerald-500/5 px-4 py-3 text-[13px] text-ink"
      >
        <div className="flex items-center gap-2">
          <Check className="h-4 w-4 text-emerald-500" strokeWidth={2.25} />
          <span className="font-medium">Automation created.</span>
          <span className="text-ink-muted">{name.trim()}</span>
        </div>
      </div>
    );
  }

  if (state.status === 'cancelled') {
    return (
      <div
        data-testid={`proposal-card-${messageId}`}
        data-status="cancelled"
        className="mb-3 rounded-2xl border border-line bg-canvas-raised px-4 py-3 text-[13px] text-ink-muted"
      >
        Proposal dismissed.
      </div>
    );
  }

  const canSubmit = name.trim().length > 0 && steps.length > 0 && state.status !== 'creating';

  return (
    <div
      data-testid={`proposal-card-${messageId}`}
      data-status={state.status}
      className="mb-3 rounded-2xl border border-accent-ring/40 bg-accent-soft/60 p-4 shadow-sm"
    >
      <div className="mb-2 flex items-center gap-2 text-[12px] font-medium uppercase tracking-[0.08em] text-accent">
        <Sparkles className="h-3.5 w-3.5" strokeWidth={2} />
        <span>New automation</span>
      </div>

      <label className="mb-2 block">
        <span className="text-[12px] font-medium text-ink-muted">Name</span>
        <input
          data-testid={`proposal-card-${messageId}-name`}
          type="text"
          value={name}
          onChange={(e) => setName(e.target.value)}
          maxLength={80}
          className="mt-1 block w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 text-[14px] text-ink focus:border-accent-ring focus:outline-none focus:ring-2 focus:ring-accent/20"
        />
      </label>

      <label className="mb-2 block">
        <span className="text-[12px] font-medium text-ink-muted">Description</span>
        <input
          data-testid={`proposal-card-${messageId}-description`}
          type="text"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          maxLength={200}
          placeholder="Optional"
          className="mt-1 block w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 text-[14px] text-ink focus:border-accent-ring focus:outline-none focus:ring-2 focus:ring-accent/20"
        />
      </label>

      <label className="mb-2 block">
        <span className="text-[12px] font-medium text-ink-muted">
          Steps · one per line
        </span>
        <textarea
          data-testid={`proposal-card-${messageId}-steps`}
          value={stepsText}
          onChange={(e) => setStepsText(e.target.value)}
          rows={Math.max(3, Math.min(8, steps.length + 1))}
          className="mt-1 block w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 font-mono text-[13px] text-ink focus:border-accent-ring focus:outline-none focus:ring-2 focus:ring-accent/20"
        />
      </label>

      <div className="mb-3 flex items-center gap-2 text-[12px] text-ink-muted">
        <Calendar className="h-3.5 w-3.5" strokeWidth={2} />
        <span>Schedule:</span>
        <span data-testid={`proposal-card-${messageId}-schedule`} className="font-medium text-ink">
          {scheduleLabel}
        </span>
        {schedule && schedule.kind !== 'off' ? (
          <button
            type="button"
            onClick={() => setSchedule({ kind: 'off', cron: null, runAt: null, timezone: schedule.timezone ?? null })}
            className="text-[11px] text-ink-subtle underline underline-offset-2 hover:text-ink"
          >
            clear
          </button>
        ) : null}
      </div>
      <p className="mb-3 text-[11px] text-ink-subtle">
        You can fine-tune the schedule after creating — open Automations → this item.
      </p>

      {state.error ? (
        <p
          data-testid={`proposal-card-${messageId}-error`}
          className="mb-2 text-[12px] text-rose-500"
        >
          {state.error}
        </p>
      ) : null}

      <div className="flex items-center justify-end gap-2">
        <button
          type="button"
          data-testid={`proposal-card-${messageId}-cancel`}
          onClick={onCancel}
          disabled={isTerminal || state.status === 'creating'}
          className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3.5 py-1.5 text-sm font-medium text-ink transition-colors hover:bg-canvas-sunken disabled:opacity-50"
        >
          <X className="h-4 w-4" strokeWidth={1.75} />
          Cancel
        </button>
        <button
          type="button"
          data-testid={`proposal-card-${messageId}-create`}
          onClick={onCreate}
          disabled={!canSubmit}
          className="inline-flex items-center gap-1.5 rounded-full bg-accent px-3.5 py-1.5 text-sm font-medium text-canvas transition-colors hover:bg-accent-hover disabled:opacity-50"
        >
          {state.status === 'creating' ? (
            <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.75} />
          ) : (
            <Plus className="h-4 w-4" strokeWidth={2} />
          )}
          Create
        </button>
      </div>
    </div>
  );
}

function describeSchedule(schedule: AutomationSchedule | null | undefined): string {
  if (!schedule || schedule.kind === 'off') return 'Manual — run on demand';
  if (schedule.kind === 'one-shot') {
    if (!schedule.runAt) return 'One time (time not set)';
    try {
      return `Once at ${new Date(schedule.runAt).toLocaleString()}`;
    } catch {
      return `Once at ${schedule.runAt}`;
    }
  }
  if (schedule.kind === 'cron' && schedule.cron) {
    return `Cron · ${schedule.cron}`;
  }
  return 'Scheduled';
}
