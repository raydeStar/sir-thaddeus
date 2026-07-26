import { useEffect, useMemo, useState } from 'react';
import {
  Check,
  ChevronDown,
  Circle,
  Hand,
  MessageSquarePlus,
  Octagon,
  Pause,
  Play,
  Wrench,
  XCircle,
} from 'lucide-react';
import { useToolActivityStore } from '../stores/toolActivityStore';
import type { TurnRunState, WorkPlan } from '@thaddeus/shared-types';

export interface SteerableProgressCardProps {
  messageId: string;
  startedAt?: number;
  hasVisibleText: boolean;
  runState?: TurnRunState;
  checkpoint?: string | null;
  plan?: WorkPlan | null;
  onPauseResume: () => void;
  onRedirect: () => void;
  onTakeOver: () => void;
  onStop: () => void;
}

/**
 * Live work surface backed only by observed runtime events. It deliberately
 * avoids synthetic percentages or invented steps: each completed row maps to
 * an actual tool event, and the initial preparation row maps to the turn.
 */
export function SteerableProgressCard({
  messageId,
  startedAt,
  hasVisibleText,
  runState = 'running',
  checkpoint,
  plan,
  onPauseResume,
  onRedirect,
  onTakeOver,
  onStop,
}: SteerableProgressCardProps) {
  const activities = useToolActivityStore((state) => state.byMessage[messageId]) ?? EMPTY;
  const [expanded, setExpanded] = useState(true);
  const [elapsedSeconds, setElapsedSeconds] = useState(0);

  useEffect(() => {
    const origin = startedAt ?? Date.now();
    const update = () => setElapsedSeconds(Math.max(0, Math.floor((Date.now() - origin) / 1000)));
    update();
    const interval = window.setInterval(update, 1000);
    return () => window.clearInterval(interval);
  }, [startedAt]);

  const active = activities.find((activity) => activity.status === 'running');
  const activePlanStep = plan?.steps.find((step) => step.status === 'active');
  const completed = activities.filter((activity) => activity.status !== 'running');
  const statusLine = useMemo(() => {
    if (runState === 'paused') return 'Paused safely';
    if (runState === 'pausing') return 'Pausing at the next safe checkpoint';
    if (runState === 'takingover' || runState === 'taking_over') return 'Waiting for you at a safe checkpoint';
    if (runState === 'cancelling') return 'Stopping active work';
    if (active) return presentActivity(active.group);
    if (activePlanStep) return activePlanStep.label;
    if (hasVisibleText) return 'Composing the response';
    if (completed.length > 0) return 'Checking the result';
    return 'Preparing the response';
  }, [active, activePlanStep, completed.length, hasVisibleText, runState]);
  const paused =
    runState === 'paused' ||
    runState === 'pausing' ||
    runState === 'takingover' ||
    runState === 'taking_over';
  const controlsDisabled = runState === 'cancelling' || runState === 'cancelled';

  return (
    <section
      role="group"
      aria-label="Current work"
      className="steerable-progress-card"
      data-testid="steerable-progress-card"
    >
      <button
        type="button"
        className="flex min-h-12 w-full items-center gap-3 px-3.5 py-3 text-left"
        onClick={() => setExpanded((value) => !value)}
        aria-expanded={expanded}
      >
        <span className={paused ? 'agent-paused-dot' : 'agent-breathing-dot'} aria-hidden />
        <span className="min-w-0 flex-1">
          <strong className="block text-xs font-semibold text-ink">Working on your request</strong>
          <span className="mt-0.5 block truncate text-[11px] text-ink-muted">
            {statusLine} - {formatElapsed(elapsedSeconds)} - you remain in control
          </span>
        </span>
        <ChevronDown className={`h-4 w-4 text-ink-subtle transition-transform ${expanded ? 'rotate-180' : ''}`} />
      </button>

      {expanded ? (
        <div className="border-t border-line px-3.5 py-3">
          <ol className="space-y-2" role="list" aria-label="Observed work steps">
            <ProgressStep status="done" label="Request received" />
            {plan?.steps.map((step) => (
              <ProgressStep
                key={step.stepId}
                status={
                  step.status === 'blocked'
                    ? 'error'
                    : step.status === 'skipped'
                      ? 'skipped'
                      : step.status
                }
                label={step.label}
                detail={step.requiresPermission ? 'permission at point of use' : undefined}
              />
            ))}
            {activities.map((activity) => (
              <ProgressStep
                key={activity.activityId}
                status={activity.status === 'running' ? 'active' : activity.status === 'error' ? 'error' : 'done'}
                label={activity.status === 'running' ? presentActivity(activity.group) : pastActivity(activity.group)}
                detail={activity.tool}
              />
            ))}
            {!plan ? (
              <ProgressStep
                status={paused ? 'pending' : active ? 'pending' : 'active'}
                label={paused
                  ? `Paused${checkpoint ? ` at ${presentCheckpoint(checkpoint)}` : ''}`
                  : hasVisibleText ? 'Composing the response' : 'Preparing the response'}
              />
            ) : null}
          </ol>

          <div
            className="sr-only"
            aria-live="polite"
            aria-atomic="true"
          >
            {statusLine}
          </div>

          <div className="mt-3 flex flex-wrap gap-2 border-t border-line pt-3">
            <button
              type="button"
              className="btn-quiet min-h-9 text-xs"
              onClick={onPauseResume}
              disabled={controlsDisabled}
              data-testid="run-pause-resume"
            >
              {paused ? <Play className="h-3.5 w-3.5" /> : <Pause className="h-3.5 w-3.5" />}
              {paused ? 'Resume' : 'Pause'}
              <kbd className="text-[9px] opacity-60">Space</kbd>
            </button>
            <button type="button" className="btn-quiet min-h-9 text-xs" onClick={onRedirect}>
              <MessageSquarePlus className="h-3.5 w-3.5" />
              Redirect
            </button>
            <button type="button" className="btn-quiet min-h-9 text-xs" onClick={onTakeOver}>
              <Hand className="h-3.5 w-3.5" />
              Take over
            </button>
            <button
              type="button"
              className="ml-auto inline-flex min-h-9 items-center gap-1.5 rounded-full px-3 py-1.5 text-xs font-medium text-rose-500 transition hover:bg-rose-500/10"
              onClick={onStop}
              disabled={controlsDisabled}
            >
              <Octagon className="h-3.5 w-3.5" />
              Stop
              <kbd className="text-[9px] opacity-60">.</kbd>
            </button>
          </div>
        </div>
      ) : null}
    </section>
  );
}

function presentCheckpoint(checkpoint: string): string {
  if (checkpoint.startsWith('tool-loop:tool:')) {
    return checkpoint.slice('tool-loop:tool:'.length).replaceAll('_', ' ');
  }
  if (checkpoint.startsWith('tool-loop:model:')) return 'the next model step';
  if (checkpoint.startsWith('pipeline:')) {
    return checkpoint.slice('pipeline:'.length).replaceAll(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
  }
  return 'a safe boundary';
}

function ProgressStep({
  status,
  label,
  detail,
}: {
  status: 'pending' | 'active' | 'done' | 'skipped' | 'error';
  label: string;
  detail?: string;
}) {
  const Icon =
    status === 'done'
      ? Check
      : status === 'error'
        ? XCircle
        : status === 'active'
          ? Wrench
          : Circle;
  return (
    <li className="flex items-start gap-2 text-xs">
      <Icon
        className={`mt-0.5 h-3.5 w-3.5 shrink-0 ${
          status === 'done'
            ? 'text-emerald-600 dark:text-emerald-300'
            : status === 'error'
              ? 'text-rose-500'
              : status === 'active'
                ? 'text-accent'
                : 'text-ink-subtle'
        }`}
        strokeWidth={status === 'done' ? 2.2 : 1.8}
      />
      <span className={
        status === 'pending'
          ? 'text-ink-subtle'
          : status === 'skipped'
            ? 'text-ink-subtle line-through'
            : 'text-ink-muted'
      }>
        {label}
        {detail ? <span className="ml-1.5 font-mono text-[10px] text-ink-subtle">{detail}</span> : null}
      </span>
    </li>
  );
}

const EMPTY: ReturnType<typeof useToolActivityStore.getState>['byMessage'][string] = [];

function presentActivity(group: string): string {
  if (group === 'Screen') return 'Reading the active window';
  if (group === 'Files') return 'Reading local files';
  if (group === 'System') return 'Running a local system action';
  if (group === 'Web') return 'Retrieving current web information';
  if (group === 'MemoryRead') return 'Looking up saved memory';
  if (group === 'MemoryWrite') return 'Updating saved memory';
  return 'Using a deterministic capability';
}

function pastActivity(group: string): string {
  if (group === 'Screen') return 'Read the active window';
  if (group === 'Files') return 'Read local files';
  if (group === 'System') return 'Ran a local system action';
  if (group === 'Web') return 'Retrieved current web information';
  if (group === 'MemoryRead') return 'Looked up saved memory';
  if (group === 'MemoryWrite') return 'Updated saved memory';
  return 'Used a deterministic capability';
}

function formatElapsed(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes}m ${String(seconds % 60).padStart(2, '0')}s`;
}
