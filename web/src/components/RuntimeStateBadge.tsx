import { useRuntimeStore } from '../stores/runtimeStore';

const stateLabels: Record<string, { label: string; tone: string }> = {
  Idle: { label: 'Idle', tone: 'bg-slate-100 text-slate-700' },
  Listening: { label: 'Listening', tone: 'bg-blue-100 text-blue-700' },
  Transcribing: { label: 'Transcribing', tone: 'bg-blue-100 text-blue-700' },
  Thinking: { label: 'Thinking', tone: 'bg-violet-100 text-violet-700' },
  ExecutingTools: { label: 'Acting', tone: 'bg-amber-100 text-amber-700' },
  AwaitingPermission: { label: 'Awaiting permission', tone: 'bg-amber-100 text-amber-700' },
  Speaking: { label: 'Speaking', tone: 'bg-emerald-100 text-emerald-700' },
  Paused: { label: 'Paused', tone: 'bg-slate-200 text-slate-700' },
  Stopping: { label: 'Stopping', tone: 'bg-rose-100 text-rose-700' },
  Error: { label: 'Error', tone: 'bg-rose-100 text-rose-700' },
};

/** Top-bar pill that mirrors the runtime's authoritative state. */
export function RuntimeStateBadge() {
  const state = useRuntimeStore((s) => s.state);
  const connected = useRuntimeStore((s) => s.connected);
  const meta = stateLabels[state] ?? { label: state, tone: 'bg-slate-100 text-slate-700' };

  return (
    <div className="flex items-center gap-2" data-testid="runtime-state-badge" data-state={state}>
      <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${meta.tone}`}>
        {meta.label}
      </span>
      <span
        className={`inline-block h-2 w-2 rounded-full ${connected ? 'bg-emerald-500' : 'bg-slate-300'}`}
        title={connected ? 'Connected to runtime' : 'Disconnected from runtime'}
        data-testid="runtime-connection-dot"
        data-connected={connected}
      />
    </div>
  );
}
