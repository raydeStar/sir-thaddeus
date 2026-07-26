import { useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  ArrowDown,
  ArrowUp,
  Check,
  Pencil,
  Plus,
  ShieldAlert,
  Trash2,
  X,
} from 'lucide-react';
import type { WorkPlan, WorkPlanStep } from '@thaddeus/shared-types';

export interface PlanApprovalCardProps {
  plan: WorkPlan;
  onSave: (steps: WorkPlanStep[]) => Promise<void>;
  onApprove: () => Promise<void>;
  onCancel: () => Promise<void>;
}

export function PlanApprovalCard({
  plan,
  onSave,
  onApprove,
  onCancel,
}: PlanApprovalCardProps) {
  const [steps, setSteps] = useState<WorkPlanStep[]>(plan.steps);
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setSteps(plan.steps);
    setError(null);
  }, [plan.planId, plan.version, plan.steps]);

  const dirty = useMemo(
    () => JSON.stringify(steps) !== JSON.stringify(plan.steps),
    [plan.steps, steps],
  );

  const save = async () => {
    if (!dirty) {
      setEditing(false);
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await onSave(steps);
      setEditing(false);
    } catch (cause) {
      setError((cause as Error).message || 'Could not update the plan.');
      throw cause;
    } finally {
      setBusy(false);
    }
  };

  const approve = async () => {
    setBusy(true);
    setError(null);
    try {
      if (dirty) await onSave(steps);
      await onApprove();
    } catch (cause) {
      setError((cause as Error).message || 'Could not start this plan.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <section
      className="steerable-progress-card overflow-hidden"
      role="group"
      aria-label={`Review work plan: ${plan.intent}`}
      data-testid="plan-approval-card"
    >
      <header className="border-b border-line px-4 py-3.5">
        <div className="flex items-start gap-3">
          <span className="mt-1 flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-accent-soft text-accent">
            <Pencil className="h-3.5 w-3.5" aria-hidden />
          </span>
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="text-sm font-semibold text-ink">Review the plan before work begins</h2>
              <span className="plan-risk-badge" data-risk={plan.risk}>
                {plan.risk} risk
              </span>
            </div>
            <p className="mt-1 text-xs leading-5 text-ink-muted">{plan.intent}</p>
            <p className="mt-1 text-[11px] text-ink-subtle">{plan.riskSummary}</p>
          </div>
        </div>
      </header>

      <div className="px-4 py-3.5">
        <ol className="space-y-2.5" aria-label="Planned work steps">
          {steps.map((step, index) => (
            <li
              key={step.stepId}
              className="flex min-h-10 items-start gap-2 rounded-xl border border-line bg-canvas-sunken/45 px-2.5 py-2"
            >
              <span className="mt-1 flex h-5 w-5 shrink-0 items-center justify-center rounded-full border border-line text-[10px] font-semibold text-ink-subtle">
                {index + 1}
              </span>
              {editing ? (
                <input
                  value={step.label}
                  onChange={(event) => {
                    const label = event.currentTarget.value;
                    setSteps((current) =>
                      current.map((candidate) =>
                        candidate.stepId === step.stepId ? { ...candidate, label } : candidate,
                      ),
                    );
                  }}
                  className="min-h-8 min-w-0 flex-1 rounded-lg border border-line bg-canvas px-2 text-xs text-ink outline-none focus:border-accent"
                  aria-label={`Step ${index + 1}`}
                  maxLength={180}
                />
              ) : (
                <span className="min-w-0 flex-1 text-xs leading-5 text-ink-muted">
                  {step.label}
                  {step.requiresPermission ? (
                    <span className="ml-2 inline-flex items-center gap-1 text-[10px] text-amber-700 dark:text-amber-300">
                      <ShieldAlert className="h-3 w-3" />
                      permission at point of use
                    </span>
                  ) : null}
                </span>
              )}
              {editing ? (
                <div className="flex shrink-0 items-center">
                  <PlanIconButton
                    label={`Move step ${index + 1} up`}
                    disabled={index === 0}
                    onClick={() => setSteps((current) => move(current, index, index - 1))}
                  >
                    <ArrowUp className="h-3.5 w-3.5" />
                  </PlanIconButton>
                  <PlanIconButton
                    label={`Move step ${index + 1} down`}
                    disabled={index === steps.length - 1}
                    onClick={() => setSteps((current) => move(current, index, index + 1))}
                  >
                    <ArrowDown className="h-3.5 w-3.5" />
                  </PlanIconButton>
                  <PlanIconButton
                    label={`Remove step ${index + 1}`}
                    disabled={steps.length === 1}
                    onClick={() =>
                      setSteps((current) => current.filter((candidate) => candidate.stepId !== step.stepId))
                    }
                  >
                    <Trash2 className="h-3.5 w-3.5" />
                  </PlanIconButton>
                </div>
              ) : null}
            </li>
          ))}
        </ol>

        {editing && steps.length < 12 ? (
          <button
            type="button"
            className="btn-quiet mt-2 min-h-9 text-xs"
            onClick={() => setSteps((current) => [...current, newGeneralStep()])}
          >
            <Plus className="h-3.5 w-3.5" />
            Add step
          </button>
        ) : null}

        {error ? (
          <p className="mt-3 text-xs text-rose-600 dark:text-rose-300" role="alert">
            {error}
          </p>
        ) : null}

        <div className="mt-3 flex flex-wrap items-center gap-2 border-t border-line pt-3">
          {editing ? (
            <>
              <button
                type="button"
                className="btn-quiet min-h-9 text-xs"
                disabled={busy || steps.some((step) => !step.label.trim())}
                onClick={() => void save()}
              >
                <Check className="h-3.5 w-3.5" />
                Save changes
              </button>
              <button
                type="button"
                className="btn-quiet min-h-9 text-xs"
                disabled={busy}
                onClick={() => {
                  setSteps(plan.steps);
                  setEditing(false);
                }}
              >
                Cancel edit
              </button>
            </>
          ) : (
            <button
              type="button"
              className="btn-quiet min-h-9 text-xs"
              disabled={busy}
              onClick={() => setEditing(true)}
              data-testid="plan-edit"
            >
              <Pencil className="h-3.5 w-3.5" />
              Edit plan
            </button>
          )}

          <button
            type="button"
            className="ml-auto inline-flex min-h-9 items-center gap-1.5 rounded-full px-3 py-1.5 text-xs font-medium text-rose-500 transition hover:bg-rose-500/10"
            disabled={busy}
            onClick={() => void onCancel()}
          >
            <X className="h-3.5 w-3.5" />
            Cancel
          </button>
          <button
            type="button"
            className="btn-primary min-h-9 px-4 text-xs"
            disabled={busy || steps.some((step) => !step.label.trim())}
            onClick={() => void approve()}
            data-testid="plan-approve"
          >
            <Check className="h-3.5 w-3.5" />
            Start approved plan
          </button>
        </div>
      </div>
    </section>
  );
}

function PlanIconButton({
  label,
  disabled,
  onClick,
  children,
}: {
  label: string;
  disabled: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      className="wiki-icon-button h-7 w-7"
      aria-label={label}
      disabled={disabled}
      onClick={onClick}
    >
      {children}
    </button>
  );
}

function move<T>(items: T[], from: number, to: number): T[] {
  if (to < 0 || to >= items.length) return items;
  const next = [...items];
  const [item] = next.splice(from, 1);
  next.splice(to, 0, item);
  return next;
}

function newGeneralStep(): WorkPlanStep {
  return {
    stepId: `step_${crypto.randomUUID().replaceAll('-', '').slice(0, 24)}`,
    label: 'Complete the additional requested step',
    capability: 'general',
    risk: 'low',
    requiresPermission: false,
    status: 'pending',
  };
}
