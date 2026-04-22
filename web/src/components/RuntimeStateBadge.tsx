import { useRuntimeStore } from '../stores/runtimeStore';

const stateLabels: Record<string, { label: string; tone: string; dot: string }> = {
  Idle: { label: 'Ready', tone: 'text-ink-muted', dot: 'bg-emerald-500' },
  Listening: { label: 'Listening', tone: 'text-ink', dot: 'bg-sky-500' },
  Transcribing: { label: 'Transcribing', tone: 'text-ink', dot: 'bg-sky-500' },
  Thinking: { label: 'Thinking', tone: 'text-ink', dot: 'bg-violet-500' },
  ExecutingTools: { label: 'Acting', tone: 'text-ink', dot: 'bg-amber-500' },
  AwaitingPermission: { label: 'Permission', tone: 'text-ink', dot: 'bg-amber-500' },
  Speaking: { label: 'Speaking', tone: 'text-ink', dot: 'bg-emerald-500' },
  Paused: { label: 'Paused', tone: 'text-ink-muted', dot: 'bg-ink-subtle' },
  Stopping: { label: 'Stopping', tone: 'text-ink-muted', dot: 'bg-rose-500' },
  Error: { label: 'Error', tone: 'text-rose-500', dot: 'bg-rose-500' },
};

/** Quiet status pill that mirrors the runtime's authoritative state. */
export function RuntimeStateBadge() {
  const state = useRuntimeStore((s) => s.state);
  const connected = useRuntimeStore((s) => s.connected);
  const meta = stateLabels[state] ?? { label: state, tone: 'text-ink-muted', dot: 'bg-ink-subtle' };

  return (
    <div
      className="flex items-center gap-2 rounded-full border border-line bg-canvas-raised px-2.5 py-1"
      data-testid="runtime-state-badge"
      data-state={state}
    >
      <span
        className={`inline-block h-1.5 w-1.5 rounded-full ${connected ? meta.dot : 'bg-ink-subtle'}`}
        title={connected ? 'Connected to runtime' : 'Disconnected from runtime'}
        data-testid="runtime-connection-dot"
        data-connected={connected}
      />
      <span className={`text-[11px] font-medium tracking-wide ${meta.tone}`}>{meta.label}</span>
    </div>
  );
}
