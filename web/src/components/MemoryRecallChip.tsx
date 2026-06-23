import { useEffect, useState } from 'react';
import { Brain, ChevronDown } from 'lucide-react';
import { useMemoryRecallStore } from '../stores/memoryRecallStore';

interface MemoryRecallChipProps {
  messageId: string;
}

/**
 * Small "memory was pulled" chip rendered above the assistant reply.
 * Appears only when the per-turn retrieval (memory_retrieve) returned at
 * least one item — facts, events, chunks, or nuggets. Closes the trust
 * loop on dynamic recall: without this chip, the user has no way to tell
 * whether the assistant actually recalled a stored fact or generated the
 * same answer from training.
 *
 * Collapsed form shows total count + breakdown. Expanded form shows the
 * preview text (first ~200 chars of the assembled memory pack) plus the
 * exact counts per kind.
 *
 * Subscribes to {@link useMemoryRecallStore}, which receives
 * <code>chat.memory.recalled</code> WebSocket events. Mirrors the
 * FootmanDecisionChip pattern so the two chips stack consistently above
 * the assistant message.
 */
export function MemoryRecallChip({ messageId }: MemoryRecallChipProps) {
  const start = useMemoryRecallStore((s) => s.start);
  const recall = useMemoryRecallStore((s) => s.byMessage[messageId]);
  const [expanded, setExpanded] = useState(false);

  useEffect(() => {
    start();
  }, [start]);

  if (!recall) return null;

  const total =
    recall.factsCount +
    recall.eventsCount +
    recall.chunksCount +
    recall.nuggetsCount;

  if (total === 0) return null;

  const headline = `Recalled ${total} ${total === 1 ? 'memory' : 'memories'}`;
  const breakdown = formatBreakdown(recall);

  return (
    <div
      data-testid={`chat-memory-recall-chip-${messageId}`}
      data-total={total}
      className="mb-1.5 rounded-xl border border-line bg-canvas-raised text-[13px] text-ink-muted transition-colors"
    >
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        aria-expanded={expanded}
        className="flex w-full items-center gap-2 px-3 py-2 text-left"
      >
        <span className="flex h-5 w-5 shrink-0 items-center justify-center text-ink-subtle">
          <Brain className="h-4 w-4" strokeWidth={1.75} />
        </span>
        <span className="flex-1 truncate">
          {headline}
          {breakdown ? (
            <span className="ml-2 font-mono text-[12px] text-ink-subtle">{breakdown}</span>
          ) : null}
        </span>
        <span className="shrink-0 text-[11px] text-ink-subtle">
          {formatElapsed(recall.durationMs)}
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
        <div className="border-t border-line px-3 py-2 text-[12px]">
          {recall.preview ? (
            <pre
              data-testid={`chat-memory-recall-preview-${messageId}`}
              className="mb-2 max-h-40 overflow-auto whitespace-pre-wrap break-words font-sans text-[12px] leading-[1.45] text-ink"
            >
              {recall.preview}
            </pre>
          ) : (
            <p className="mb-2 italic text-ink-subtle">No preview captured.</p>
          )}
          <div className="grid grid-cols-[max-content_1fr] gap-x-3 gap-y-1">
            <span className="text-ink-subtle">Facts</span>
            <span className="font-mono text-ink">{recall.factsCount}</span>
            <span className="text-ink-subtle">Events</span>
            <span className="font-mono text-ink">{recall.eventsCount}</span>
            <span className="text-ink-subtle">Chunks</span>
            <span className="font-mono text-ink">{recall.chunksCount}</span>
            <span className="text-ink-subtle">Notes</span>
            <span className="font-mono text-ink">{recall.nuggetsCount}</span>
          </div>
        </div>
      ) : null}
    </div>
  );
}

function formatBreakdown(r: {
  factsCount: number;
  eventsCount: number;
  chunksCount: number;
  nuggetsCount: number;
}): string {
  // Compact one-line summary like "2 facts · 1 note" omitting zero buckets.
  const parts: string[] = [];
  if (r.factsCount > 0) parts.push(`${r.factsCount} ${r.factsCount === 1 ? 'fact' : 'facts'}`);
  if (r.eventsCount > 0) parts.push(`${r.eventsCount} ${r.eventsCount === 1 ? 'event' : 'events'}`);
  if (r.nuggetsCount > 0) parts.push(`${r.nuggetsCount} ${r.nuggetsCount === 1 ? 'note' : 'notes'}`);
  if (r.chunksCount > 0) parts.push(`${r.chunksCount} ${r.chunksCount === 1 ? 'chunk' : 'chunks'}`);
  return parts.join(' · ');
}

function formatElapsed(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)} s`;
}
