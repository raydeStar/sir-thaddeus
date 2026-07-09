import { useEffect, useState, useMemo } from 'react';
import { AlertTriangle, Globe, FolderOpen, Monitor, Terminal, BookOpen, Pencil, Shield } from 'lucide-react';
import { usePermissionsStore } from '../stores/permissionsStore';
import type { PermissionResponse } from '../lib/permissionsApi';

/**
 * Local record of the scope checkbox for the prompt it was toggled on.
 * Keyed by request id so the choice resets to the request's own default
 * whenever a different prompt reaches the head of the queue.
 */
interface ScopeChoice {
  id: string;
  group: boolean;
}

const groupMeta: Record<string, { label: string; icon: typeof Shield; blurb: string }> = {
  Screen: { label: 'Screen', icon: Monitor, blurb: 'Capture a screenshot or inspect the active window.' },
  Files: { label: 'Files', icon: FolderOpen, blurb: 'Read local files and directory listings.' },
  System: { label: 'System', icon: Terminal, blurb: 'Run shell commands or touch the clipboard.' },
  Web: { label: 'Web', icon: Globe, blurb: 'Reach out to the internet.' },
  MemoryRead: { label: 'Memory (read)', icon: BookOpen, blurb: 'Look up stored facts and preferences.' },
  MemoryWrite: { label: 'Memory (write)', icon: Pencil, blurb: 'Save, update, or remove stored facts.' },
};

/**
 * Top-of-stack permission prompt. Renders nothing until a request appears
 * in <code>usePermissionsStore().queue</code>. Shows the first pending
 * request; a tiny counter signals when others are waiting behind it so the
 * user knows there are multiple calls to approve.
 */
export function PermissionModal() {
  const start = usePermissionsStore((s) => s.start);
  const queue = usePermissionsStore((s) => s.queue);
  const resolve = usePermissionsStore((s) => s.resolve);
  const [submitting, setSubmitting] = useState<PermissionResponse | null>(null);
  const [scopeChoice, setScopeChoice] = useState<ScopeChoice | null>(null);

  useEffect(() => { start(); }, [start]);

  const current = queue[0];
  const queued = queue.length;

  // Checked = apply the decision group-wide. Defaults from the request's own
  // scope (missing scope = 'group'); a manual toggle only sticks for the
  // request it was made on.
  const groupScope =
    current && scopeChoice?.id === current.id
      ? scopeChoice.group
      : current
        ? current.scope !== 'tool'
        : true;

  const prettyArgs = useMemo(() => {
    if (!current) return '';
    try {
      return JSON.stringify(JSON.parse(current.argsJson), null, 2);
    } catch {
      return current.argsJson;
    }
  }, [current]);

  if (!current) return null;

  const meta = groupMeta[current.group] ?? {
    label: current.group,
    icon: Shield,
    blurb: 'Classified access request.',
  };
  const Icon = meta.icon;

  const act = async (decision: PermissionResponse) => {
    if (submitting) return;
    setSubmitting(decision);
    try {
      await resolve(current.id, decision, groupScope ? 'group' : 'tool');
    } finally {
      setSubmitting(null);
    }
  };

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="permission-modal-title"
      data-testid="permission-modal"
      className="fixed inset-0 z-50 flex items-center justify-center bg-canvas/60 px-4 backdrop-blur-sm"
    >
      <div className="w-full max-w-md rounded-2xl border border-line bg-canvas-raised p-6 shadow-lift">
        <div className="flex items-start gap-3">
          <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent">
            <Icon className="h-5 w-5" strokeWidth={1.75} />
          </span>
          <div className="min-w-0 flex-1">
            <p className="text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
              {meta.label}
              {queued > 1 ? (
                <span className="ml-2 rounded-full bg-canvas-sunken px-2 py-0.5 text-[10px] font-medium text-ink-muted">
                  +{queued - 1} more pending
                </span>
              ) : null}
            </p>
            <h2
              id="permission-modal-title"
              data-testid="permission-modal-tool"
              className="mt-1 text-[17px] font-semibold tracking-tight text-ink"
            >
              Allow <span className="font-mono text-[15px]">{current.tool}</span>?
            </h2>
            <p className="mt-1 text-[13px] text-ink-muted">{meta.blurb}</p>
          </div>
        </div>

        <div className="mt-4">
          <p className="text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
            Arguments
          </p>
          <pre
            data-testid="permission-modal-args"
            className="mt-1.5 max-h-40 overflow-auto rounded-xl bg-canvas-sunken p-3 font-mono text-[12px] leading-[1.5] text-ink"
          >
            {prettyArgs || '(none)'}
          </pre>
        </div>

        <label className="mt-4 flex cursor-pointer items-start gap-2.5">
          <input
            type="checkbox"
            data-testid="permission-scope-checkbox"
            checked={groupScope}
            onChange={(e) => setScopeChoice({ id: current.id, group: e.target.checked })}
            className="mt-0.5 h-4 w-4 shrink-0 rounded border-line accent-accent"
          />
          <span className="min-w-0">
            <span className="block text-[13px] font-medium text-ink">
              Apply to all {meta.label} tools
            </span>
            <span className="mt-0.5 block text-[11px] text-ink-subtle">
              Deny and Allow once always apply to this call only.
            </span>
          </span>
        </label>

        <div className="mt-5 flex flex-wrap items-center gap-2">
          <button
            type="button"
            data-testid="permission-deny"
            onClick={() => act('deny')}
            disabled={!!submitting}
            className="inline-flex items-center gap-1.5 rounded-full border border-rose-500/30 px-3.5 py-2 text-sm font-medium text-rose-500 transition-colors hover:bg-rose-500/10 disabled:opacity-50"
          >
            <AlertTriangle className="h-3.5 w-3.5" strokeWidth={1.75} />
            Deny
          </button>
          <div className="flex-1" />
          <button
            type="button"
            data-testid="permission-once"
            onClick={() => act('once')}
            disabled={!!submitting}
            className="rounded-full border border-line px-3.5 py-2 text-sm font-medium text-ink transition-colors hover:bg-accent-soft disabled:opacity-50"
          >
            Allow once
          </button>
          <button
            type="button"
            data-testid="permission-session"
            onClick={() => act('session')}
            disabled={!!submitting}
            className="rounded-full border border-line px-3.5 py-2 text-sm font-medium text-ink transition-colors hover:bg-accent-soft disabled:opacity-50"
          >
            For session
          </button>
          <button
            type="button"
            data-testid="permission-always"
            onClick={() => act('always')}
            disabled={!!submitting}
            className="rounded-full bg-accent px-4 py-2 text-sm font-medium text-white transition hover:opacity-90 disabled:opacity-50"
          >
            Always
          </button>
        </div>
        <p className="mt-3 text-center text-[11px] text-ink-subtle">
          "Always" updates your Settings for this category. You can change it in Settings → Permissions.
        </p>
      </div>
    </div>
  );
}
