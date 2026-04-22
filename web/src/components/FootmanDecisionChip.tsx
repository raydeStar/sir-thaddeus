import { useEffect, useState } from 'react';
import { ChevronDown, Shield, ShieldAlert, ShieldCheck } from 'lucide-react';
import { useFootmanDecisionStore } from '../stores/footmanDecisionStore';

interface FootmanDecisionChipProps {
  messageId: string;
}

/**
 * Small "gatekeeper ran" chip rendered above the assistant reply. Appears
 * only when the footman actually fired for a turn. Collapsed form shows
 * tools-kept/total and elapsed ms; expanded form shows state, confidence,
 * and reason code for debugging small-model routing behavior.
 *
 * Subscribes to {@link useFootmanDecisionStore}, which receives
 * <code>chat.footman.decision</code> WebSocket events.
 */
export function FootmanDecisionChip({ messageId }: FootmanDecisionChipProps) {
  const start = useFootmanDecisionStore((s) => s.start);
  const decision = useFootmanDecisionStore((s) => s.byMessage[messageId]);
  const [expanded, setExpanded] = useState(false);

  useEffect(() => {
    start();
  }, [start]);

  if (!decision) return null;

  const isError = decision.reasonCode === 'footman_error' || decision.reasonCode === 'footman_timeout';
  const narrowed = decision.toolsKept < decision.toolsTotal;
  const Icon = isError ? ShieldAlert : narrowed ? ShieldCheck : Shield;

  const headline = isError
    ? 'Gatekeeper unavailable'
    : narrowed
      ? `Gatekeeper kept ${decision.toolsKept} of ${decision.toolsTotal} tools`
      : `Gatekeeper kept all ${decision.toolsTotal} tools`;

  return (
    <div
      data-testid={`chat-footman-chip-${messageId}`}
      data-reason={decision.reasonCode}
      data-abstain={decision.abstain}
      className={
        'mb-1.5 rounded-xl border text-[13px] transition-colors ' +
        (isError
          ? 'border-rose-500/30 bg-canvas-raised text-ink-muted'
          : 'border-line bg-canvas-raised text-ink-muted')
      }
    >
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        aria-expanded={expanded}
        className="flex w-full items-center gap-2 px-3 py-2 text-left"
      >
        <span
          className={
            'flex h-5 w-5 shrink-0 items-center justify-center ' +
            (isError ? 'text-rose-500' : 'text-ink-subtle')
          }
        >
          <Icon className="h-4 w-4" strokeWidth={1.75} />
        </span>
        <span className="flex-1 truncate">
          {headline}
          <span className="ml-2 font-mono text-[12px] text-ink-subtle">
            {decision.nextState}
          </span>
        </span>
        <span className="shrink-0 text-[11px] text-ink-subtle">
          {formatElapsed(decision.elapsedMs)}
        </span>
        <ChevronDown
          className={
            'h-3.5 w-3.5 shrink-0 text-ink-subtle transition-transform ' +
            (expanded ? 'rotate-180' : '')
          }
          strokeWidth={1.75}
        />
      </button>
      {expanded ? (
        <div className="grid grid-cols-[max-content_1fr] gap-x-3 gap-y-1 border-t border-line px-3 py-2 text-[12px]">
          <span className="text-ink-subtle">State</span>
          <span className="font-mono text-ink">{decision.nextState}</span>
          <span className="text-ink-subtle">Confidence</span>
          <span className="font-mono text-ink">{decision.confidence.toFixed(2)}</span>
          <span className="text-ink-subtle">Abstain</span>
          <span className="font-mono text-ink">{decision.abstain ? 'true' : 'false'}</span>
          <span className="text-ink-subtle">Reason</span>
          <span className="font-mono text-ink">{decision.reasonCode || '—'}</span>
          <span className="text-ink-subtle">Tools</span>
          <span className="font-mono text-ink">
            {decision.toolsKept} kept / {decision.toolsTotal} total
          </span>
        </div>
      ) : null}
    </div>
  );
}

function formatElapsed(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)} s`;
}
