import { useState, useEffect, useMemo } from 'react';
import {
  Loader2,
  Check,
  AlertCircle,
  ChevronDown,
  Globe,
  FolderOpen,
  Monitor,
  Terminal,
  BookOpen,
  Pencil,
  Sparkles,
  type LucideIcon,
} from 'lucide-react';
import { useToolActivityStore, type ToolActivity } from '../stores/toolActivityStore';

interface ToolActivityPillsProps {
  messageId: string;
}

// Module-scoped empty sentinel so the selector never produces a new [] per render.
const EMPTY_ACTIVITIES: ToolActivity[] = [];

const groupIcons: Record<string, LucideIcon> = {
  Screen: Monitor,
  Files: FolderOpen,
  System: Terminal,
  Web: Globe,
  MemoryRead: BookOpen,
  MemoryWrite: Pencil,
  Safe: Sparkles,
};

const runningLabels: Record<string, string> = {
  Screen: 'Capturing the screen',
  Files: 'Reading files',
  System: 'Running a system command',
  Web: 'Searching the web',
  MemoryRead: 'Looking up memory',
  MemoryWrite: 'Updating memory',
  Safe: 'Running a local tool',
};

const doneLabels: Record<string, string> = {
  Screen: 'Captured screen',
  Files: 'Read files',
  System: 'Ran system command',
  Web: 'Searched the web',
  MemoryRead: 'Read memory',
  MemoryWrite: 'Updated memory',
  Safe: 'Ran local tool',
};

/**
 * Renders the inline tool-activity strip above an assistant message. Each
 * tool call gets one pill. Running pills show a spinner + terracotta accent;
 * completed pills collapse to a muted chip that expands on click.
 *
 * Subscribes to <code>useToolActivityStore</code>, which receives
 * <code>chat.tool.started</code> / <code>chat.tool.completed</code> WS
 * events from the runtime.
 */
export function ToolActivityPills({ messageId }: ToolActivityPillsProps) {
  const start = useToolActivityStore((s) => s.start);
  // Select the (potentially undefined) array directly — zustand's default
  // equality will see a stable reference across renders when nothing
  // changed. Fallback to empty happens outside the selector to avoid a
  // fresh [] allocation per render (that would trigger React #185).
  const activitiesForMessage = useToolActivityStore((s) => s.byMessage[messageId]);
  const activities = activitiesForMessage ?? EMPTY_ACTIVITIES;

  useEffect(() => {
    start();
  }, [start]);

  if (activities.length === 0) return null;

  return (
    <div
      data-testid={`chat-tool-activity-${messageId}`}
      className="mb-3 flex flex-col gap-1.5"
    >
      {activities.map((a) => (
        <ToolActivityPill key={a.activityId} activity={a} />
      ))}
    </div>
  );
}

function ToolActivityPill({ activity }: { activity: ToolActivity }) {
  const [expanded, setExpanded] = useState(false);
  const Icon = groupIcons[activity.group] ?? Sparkles;
  const isRunning = activity.status === 'running';
  const isError = activity.status === 'error';

  // Pill label. While running we lean into present tense ("Searching…");
  // once complete we switch to past tense and surface the tool name in
  // a muted mono font so the reader can see exactly what ran.
  const label = useMemo(() => {
    if (isRunning) {
      return runningLabels[activity.group] ?? 'Running a tool';
    }
    return doneLabels[activity.group] ?? 'Ran a tool';
  }, [activity.group, isRunning]);

  const duration = typeof activity.durationMs === 'number'
    ? formatDuration(activity.durationMs)
    : null;

  return (
    <div
      data-testid={`chat-tool-pill-${activity.activityId}`}
      data-status={activity.status}
      className={
        'rounded-xl border text-[13px] transition-colors ' +
        (isRunning
          ? 'border-accent-ring/40 bg-accent-soft text-ink'
          : isError
            ? 'border-rose-500/30 bg-canvas-raised text-ink'
            : 'border-line bg-canvas-raised text-ink-muted')
      }
    >
      <button
        type="button"
        onClick={() => !isRunning && setExpanded((v) => !v)}
        disabled={isRunning}
        aria-expanded={expanded}
        className="flex w-full items-center gap-2 px-3 py-2 text-left"
      >
        <span className={
          'flex h-5 w-5 shrink-0 items-center justify-center ' +
          (isRunning ? 'text-accent' : isError ? 'text-rose-500' : 'text-ink-subtle')
        }>
          {isRunning ? (
            <Loader2 className="h-4 w-4 animate-spin" strokeWidth={2} />
          ) : isError ? (
            <AlertCircle className="h-4 w-4" strokeWidth={2} />
          ) : (
            <Check className="h-4 w-4" strokeWidth={2.25} />
          )}
        </span>
        <Icon className="h-4 w-4 shrink-0 text-ink-subtle" strokeWidth={1.75} />
        <span className="flex-1 truncate">
          <span className={isRunning ? 'font-medium' : ''}>{label}</span>
          <span className="ml-2 font-mono text-[12px] text-ink-subtle">
            {activity.tool}
          </span>
        </span>
        {duration ? (
          <span className="shrink-0 text-[11px] text-ink-subtle">{duration}</span>
        ) : null}
        {!isRunning ? (
          <ChevronDown
            className={
              'h-3.5 w-3.5 shrink-0 text-ink-subtle transition-transform ' +
              (expanded ? 'rotate-180' : '')
            }
            strokeWidth={1.75}
          />
        ) : null}
      </button>
      {expanded && !isRunning ? (
        <div className="border-t border-line px-3 py-2 text-[12px] text-ink-muted">
          <div className="mb-1 text-[10px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
            Arguments
          </div>
          <pre className="mb-2 max-h-32 overflow-auto whitespace-pre-wrap font-mono text-[11px] text-ink">
            {prettyJson(activity.argsPreview)}
          </pre>
          {activity.error ? (
            <>
              <div className="mt-2 text-[10px] font-medium uppercase tracking-[0.08em] text-rose-500">
                Error
              </div>
              <p className="text-[12px] text-rose-500">{activity.error}</p>
            </>
          ) : activity.resultSnippet ? (
            <>
              <div className="mt-2 text-[10px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
                Result
              </div>
              <pre className="max-h-40 overflow-auto whitespace-pre-wrap font-mono text-[11px] text-ink-muted">
                {activity.resultSnippet}
              </pre>
            </>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

function prettyJson(raw: string): string {
  if (!raw) return '(none)';
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)} s`;
}
