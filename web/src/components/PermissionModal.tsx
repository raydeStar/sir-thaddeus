import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  AlertTriangle,
  BookOpen,
  Check,
  FolderOpen,
  Globe,
  Monitor,
  Pencil,
  Shield,
  Terminal,
  type LucideIcon,
} from 'lucide-react';
import { usePermissionsStore } from '../stores/permissionsStore';
import type {
  PendingPermission,
  PermissionResponse,
  PermissionScope,
} from '../lib/permissionsApi';

interface ScopeChoice {
  id: string;
  group: boolean;
}

interface GroupMeta {
  label: string;
  icon: LucideIcon;
  action: string;
  why: string;
  boundary: string;
}

const groupMeta: Record<string, GroupMeta> = {
  Screen: {
    label: 'Screen',
    icon: Monitor,
    action: 'Read the active screen',
    why: 'to use the on-screen context needed for your request',
    boundary: 'Stays on this machine',
  },
  Files: {
    label: 'Files',
    icon: FolderOpen,
    action: 'Read local files',
    why: 'to use the local material needed for your request',
    boundary: 'Stays on this machine',
  },
  System: {
    label: 'System',
    icon: Terminal,
    action: 'Run a local system action',
    why: 'to complete the action you asked for',
    boundary: 'Runs on this machine',
  },
  Web: {
    label: 'Web',
    icon: Globe,
    action: 'Connect to the web',
    why: 'to retrieve current information for your request',
    boundary: 'Leaves this machine for the requested web service',
  },
  MemoryRead: {
    label: 'Memory read',
    icon: BookOpen,
    action: 'Read saved memory',
    why: 'to preserve useful context and continuity',
    boundary: 'Stays on this machine',
  },
  MemoryWrite: {
    label: 'Memory write',
    icon: Pencil,
    action: 'Update saved memory',
    why: 'to remember what you explicitly asked Sir Thaddeus to retain',
    boundary: 'Saved locally on this machine',
  },
};

export interface PermissionPauseCardProps {
  /** Render only prompts for this thread. Omit for the global fallback. */
  threadId?: string;
  /** Compact fallback is used outside a conversation route. */
  compact?: boolean;
}

/**
 * In-context permission pause. It keeps the surrounding work visible, defaults
 * to the narrowest grant, and exposes the exact scope before the user decides.
 */
export function PermissionPauseCard({ threadId, compact = false }: PermissionPauseCardProps) {
  const start = usePermissionsStore((s) => s.start);
  const queue = usePermissionsStore((s) => s.queue);
  const storeError = usePermissionsStore((s) => s.error);
  const resolve = usePermissionsStore((s) => s.resolve);
  const [submitting, setSubmitting] = useState<PermissionResponse | null>(null);
  const [scopeChoice, setScopeChoice] = useState<ScopeChoice | null>(null);
  const onceRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    start();
  }, [start]);

  const matchingQueue = threadId
    ? queue.filter((request) => request.threadId === threadId)
    : queue;
  const current = matchingQueue[0];
  const queued = matchingQueue.length;
  const callScoped = current?.scope === 'call';
  const groupScope =
    current && scopeChoice?.id === current.id
      ? scopeChoice.group
      : current
        ? current.scope !== 'tool'
        : true;

  const prettyArgs = useMemo(() => formatArgs(current), [current]);
  const scopePreview = useMemo(
    () => current ? buildScopePreview(current, callScoped ? 'call' : groupScope ? 'group' : 'tool') : '',
    [callScoped, current, groupScope],
  );

  const act = useCallback(async (decision: PermissionResponse) => {
    if (!current || submitting) return;
    setSubmitting(decision);
    const scope: PermissionScope = callScoped ? 'call' : groupScope ? 'group' : 'tool';
    try {
      await resolve(current.id, decision, scope);
    } finally {
      setSubmitting(null);
    }
  }, [callScoped, current, groupScope, resolve, submitting]);

  useEffect(() => {
    if (!current) return;
    const timeout = window.setTimeout(() => onceRef.current?.focus(), 0);
    return () => window.clearTimeout(timeout);
  }, [current]);

  useEffect(() => {
    if (!current) return;
    const handler = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey) return;
      const target = event.target as HTMLElement | null;
      if (target?.matches('input, textarea, select')) return;
      const decision =
        event.key === '1'
          ? 'once'
          : event.key === '2' && !callScoped
            ? 'session'
            : event.key === '3' && !callScoped
              ? 'always'
              : event.key === 'Escape'
                ? 'deny'
                : null;
      if (!decision) return;
      event.preventDefault();
      void act(decision);
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [act, callScoped, current]);

  if (!current) return null;

  const meta = groupMeta[current.group] ?? {
    label: current.group,
    icon: Shield,
    action: humanizeTool(current.tool),
    why: 'to continue the work you requested',
    boundary: 'Review the exact scope below',
  };
  const Icon = meta.icon;

  return (
    <section
      role="group"
      aria-label={`Permission required: ${meta.action.toLowerCase()}`}
      data-testid="permission-modal"
      data-permission-id={current.id}
      className={
        `permission-pause-card ${compact ? 'permission-pause-card--compact' : ''}`
      }
    >
      <div className="flex items-start gap-3">
        <span className="permission-pause-card__icon" aria-hidden>
          <Icon className="h-4 w-4" strokeWidth={1.9} />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <p className="text-[10px] font-semibold uppercase tracking-[0.12em] text-amber-700 dark:text-amber-300">
              Paused for permission
            </p>
            {queued > 1 ? (
              <span className="rounded-full border border-line px-2 py-0.5 text-[10px] text-ink-subtle">
                +{queued - 1} waiting
              </span>
            ) : null}
          </div>
          <h2
            data-testid="permission-modal-tool"
            className="mt-1 text-[15px] font-semibold tracking-tight text-ink"
          >
            {actionFor(current, meta.action)}
            <span className="ml-2 font-mono text-[11px] font-normal text-ink-subtle">
              {current.tool}
            </span>
          </h2>
          <p className="mt-1 text-xs leading-5 text-ink-muted">
            Sir Thaddeus needs this {meta.why}. The current task is paused; nothing runs until you decide.
          </p>
        </div>
      </div>

      <div className="mt-3 grid gap-2 sm:grid-cols-[minmax(0,1fr)_auto]">
        <div
          data-testid="permission-modal-args"
          className="min-w-0 rounded-xl border border-line bg-canvas-sunken px-3 py-2"
        >
          <p className="text-[10px] font-semibold uppercase tracking-[0.1em] text-ink-subtle">
            Exact scope
          </p>
          <p className="mt-1 break-words font-mono text-[11px] leading-5 text-ink">
            {scopePreview}
          </p>
          {prettyArgs ? (
            <details className="mt-1.5 text-[11px] text-ink-muted">
              <summary className="cursor-pointer select-none">Show arguments</summary>
              <pre className="mt-2 max-h-36 overflow-auto whitespace-pre-wrap font-mono text-[11px] text-ink">
                {prettyArgs}
              </pre>
            </details>
          ) : null}
        </div>
        <div className="flex items-start gap-2 rounded-xl border border-line px-3 py-2 text-[11px] text-ink-muted">
          <Shield className="mt-0.5 h-3.5 w-3.5 shrink-0 text-accent" strokeWidth={1.9} />
          <span>{meta.boundary}</span>
        </div>
      </div>

      {!callScoped ? (
        <label className="mt-3 flex cursor-pointer items-start gap-2.5 text-xs">
          <input
            type="checkbox"
            data-testid="permission-scope-checkbox"
            checked={groupScope}
            onChange={(event) => setScopeChoice({ id: current.id, group: event.target.checked })}
            className="mt-0.5 h-4 w-4 shrink-0 rounded border-line accent-accent"
          />
          <span>
            <span className="font-medium text-ink">Apply broader grants to all {meta.label} tools</span>
            <span className="mt-0.5 block text-[11px] text-ink-subtle">
              Turn this off to grant only <span className="font-mono">{current.tool}</span>.
            </span>
          </span>
        </label>
      ) : (
        <p
          data-testid="permission-call-scope-notice"
          className="mt-3 text-[11px] leading-5 text-ink-muted"
        >
          This action is always one-time. Session and permanent grants are shown below for clarity but cannot be selected.
        </p>
      )}

      {storeError ? (
        <p role="alert" className="mt-3 text-xs text-rose-500">
          {storeError}
        </p>
      ) : null}

      <div className="mt-4 flex flex-wrap items-center gap-2">
        <button
          ref={onceRef}
          type="button"
          data-testid="permission-once"
          onClick={() => void act('once')}
          disabled={Boolean(submitting)}
          className="btn-primary min-h-10"
        >
          {submitting === 'once' ? 'Allowing...' : 'Allow this time'}
          <kbd className="ml-1 text-[9px] opacity-70">1</kbd>
        </button>
        <button
          type="button"
          data-testid="permission-session"
          onClick={() => void act('session')}
          disabled={Boolean(submitting) || callScoped}
          className="btn-quiet min-h-10 disabled:opacity-35"
          title={callScoped ? 'This action must be approved each time.' : undefined}
        >
          Allow all session
          <kbd className="ml-1 text-[9px] opacity-60">2</kbd>
        </button>
        <button
          type="button"
          data-testid="permission-always"
          onClick={() => void act('always')}
          disabled={Boolean(submitting) || callScoped}
          className="btn-quiet min-h-10 disabled:opacity-35"
          title={callScoped ? 'This action cannot be permanently allowed.' : undefined}
        >
          Always allow
          <kbd className="ml-1 text-[9px] opacity-60">3</kbd>
        </button>
        <button
          type="button"
          data-testid="permission-deny"
          onClick={() => void act('deny')}
          disabled={Boolean(submitting)}
          className="ml-auto inline-flex min-h-10 items-center gap-1.5 rounded-full px-3.5 py-2 text-sm font-medium text-rose-500 transition hover:bg-rose-500/10 disabled:opacity-50"
        >
          <AlertTriangle className="h-3.5 w-3.5" strokeWidth={1.8} />
          Deny
          <kbd className="ml-1 text-[9px] opacity-60">Esc</kbd>
        </button>
      </div>

      {!callScoped ? (
        <p className="mt-3 flex items-center gap-1.5 text-[10px] text-ink-subtle">
          <Check className="h-3 w-3" aria-hidden />
          Permanent changes remain editable in Settings → Permissions.
        </p>
      ) : null}
    </section>
  );
}

/** Backward-compatible export while callers migrate from the old modal name. */
export const PermissionModal = PermissionPauseCard;

function formatArgs(current: PendingPermission | undefined): string {
  if (!current?.argsJson) return '';
  try {
    return JSON.stringify(JSON.parse(current.argsJson), null, 2);
  } catch {
    return current.argsJson;
  }
}

function buildScopePreview(current: PendingPermission, scope: PermissionScope): string {
  const lifetime =
    current.scope === 'call' || scope === 'call'
      ? 'this action only'
      : scope === 'group'
        ? `all ${current.group} tools`
        : `${current.tool} only`;
  return `${current.tool} - ${summarizeArgs(current.argsJson)} - ${lifetime}`;
}

function summarizeArgs(raw: string): string {
  if (!raw) return 'no arguments';
  try {
    const value = JSON.parse(raw) as Record<string, unknown>;
    const preferredKeys = ['path', 'query', 'url', 'name', 'title', 'command'];
    const key = preferredKeys.find((candidate) => value[candidate] != null)
      ?? Object.keys(value)[0];
    if (!key) return 'no arguments';
    const rendered = String(value[key]);
    return `${key}: ${rendered.length > 90 ? `${rendered.slice(0, 87)}...` : rendered}`;
  } catch {
    return raw.length > 90 ? `${raw.slice(0, 87)}...` : raw;
  }
}

function actionFor(current: PendingPermission, fallback: string): string {
  const tool = current.tool.toLowerCase();
  if (tool.startsWith('wiki_')) {
    if (tool.includes('create')) return 'Create local Wiki content';
    if (tool.includes('delete') || tool.includes('remove') || tool.includes('purge')) return 'Remove local Wiki content';
    if (tool.includes('update') || tool.includes('edit') || tool.includes('write')) return 'Update local Wiki content';
  }
  if (tool.includes('screen')) return 'Read the active window';
  if (tool.includes('search')) return 'Search the web';
  if (tool.includes('file') && tool.includes('read')) return 'Read a local file';
  return fallback;
}

function humanizeTool(tool: string): string {
  const words = tool.replace(/[_-]+/g, ' ').trim();
  return words ? `${words[0].toUpperCase()}${words.slice(1)}` : 'Run this action';
}
