import { Braces, ChevronRight, List as ListIcon } from 'lucide-react';
import type { TurnTraceResponse, TurnTraceSummary } from '@thaddeus/shared-types';
import { formatAbsoluteTime, formatBytes, formatRelativeTime } from './logFormatting';

export type TraceViewMode = 'events' | 'raw';

export function TurnTracePane({
  traces,
  error,
  selectedMessageId,
  selectedSummary,
  trace,
  loading,
  traceError,
  viewMode,
  onViewModeChange,
  onSelect,
}: {
  traces: TurnTraceSummary[] | null;
  error: string | null;
  selectedMessageId: string | null;
  selectedSummary: TurnTraceSummary | null;
  trace: TurnTraceResponse | null;
  loading: boolean;
  traceError: string | null;
  viewMode: TraceViewMode;
  onViewModeChange: (mode: TraceViewMode) => void;
  onSelect: (messageId: string) => void;
}) {
  if (error) {
    return (
      <p className="text-sm text-rose-500" data-testid="settings-logs-error">
        {error}
      </p>
    );
  }

  if (traces === null) {
    return (
      <p className="text-sm italic text-ink-subtle" data-testid="settings-logs-loading">
        Loading...
      </p>
    );
  }

  if (traces.length === 0) {
    return (
      <p className="text-sm italic text-ink-subtle" data-testid="settings-logs-empty">
        No turn traces yet - send a chat message and one will appear here.
      </p>
    );
  }

  return (
    <div className="grid gap-4 lg:grid-cols-[minmax(250px,0.42fr)_minmax(0,1fr)]" data-testid="settings-logs-trace-browser">
      <div className="min-w-0 overflow-hidden rounded-lg border border-line bg-canvas-raised/40">
        <div className="border-b border-line px-3 py-2 text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
          Recent traces
        </div>
        <div className="max-h-[32rem] overflow-auto" data-testid="settings-logs-list">
          {traces.map((traceSummary) => {
            const isSelected = selectedMessageId === traceSummary.messageId;
            return (
              <button
                key={traceSummary.messageId}
                type="button"
                onClick={() => onSelect(traceSummary.messageId)}
                aria-current={isSelected ? 'true' : undefined}
                data-testid={`settings-logs-row-${traceSummary.messageId}`}
                className={`flex w-full items-start justify-between gap-3 border-b border-line/70 px-3 py-3 text-left transition last:border-b-0 ${
                  isSelected ? 'bg-accent-soft/80 text-ink' : 'hover:bg-canvas-sunken/60'
                }`}
              >
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[11px] uppercase tracking-[0.08em] text-ink-subtle">
                    <span>{formatRelativeTime(traceSummary.modifiedAt)}</span>
                    <span>{traceSummary.eventCount} events</span>
                    <span>{formatBytes(traceSummary.sizeBytes)}</span>
                  </div>
                  <div className="mt-1 truncate font-mono text-[12px] text-ink">
                    {traceSummary.messageId}
                  </div>
                  <div className="mt-1 flex min-w-0 flex-wrap items-center gap-2 text-[11px] text-ink-muted">
                    {traceSummary.lastEventType ? (
                      <span className="truncate font-mono lowercase">{traceSummary.lastEventType}</span>
                    ) : null}
                    {traceSummary.threadId ? (
                      <span className="truncate">thread {traceSummary.threadId}</span>
                    ) : null}
                  </div>
                </div>
                {isSelected ? (
                  <ChevronRight className="mt-1 h-4 w-4 shrink-0 text-ink" strokeWidth={1.75} />
                ) : null}
              </button>
            );
          })}
        </div>
      </div>

      <TraceDetailPanel
        selectedMessageId={selectedMessageId}
        selectedSummary={selectedSummary}
        trace={trace}
        loading={loading}
        error={traceError}
        viewMode={viewMode}
        onViewModeChange={onViewModeChange}
      />
    </div>
  );
}

function TraceDetailPanel({
  selectedMessageId,
  selectedSummary,
  trace,
  loading,
  error,
  viewMode,
  onViewModeChange,
}: {
  selectedMessageId: string | null;
  selectedSummary: TurnTraceSummary | null;
  trace: TurnTraceResponse | null;
  loading: boolean;
  error: string | null;
  viewMode: TraceViewMode;
  onViewModeChange: (mode: TraceViewMode) => void;
}) {
  return (
    <div className="min-w-0 rounded-lg border border-line bg-canvas-raised/40" data-testid="settings-logs-trace-view">
      <div className="border-b border-line px-4 py-3">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
              Selected trace
            </div>
            <div className="mt-1 truncate font-mono text-[13px] text-ink">
              {selectedMessageId ?? 'No trace selected'}
            </div>
          </div>
          {selectedSummary ? (
            <div className="flex flex-wrap gap-2 text-[11px] text-ink-muted">
              <span>{selectedSummary.eventCount} events</span>
              <span>{formatBytes(selectedSummary.sizeBytes)}</span>
              <span>{formatAbsoluteTime(selectedSummary.modifiedAt)}</span>
            </div>
          ) : null}
        </div>
      </div>

      {selectedMessageId ? (
        <>
          <div className="flex items-center gap-2 border-b border-line px-4 py-2" role="tablist" aria-label="Trace view mode">
            <button
              type="button"
              role="tab"
              aria-selected={viewMode === 'events'}
              data-testid="settings-logs-tab-events"
              onClick={() => onViewModeChange('events')}
              className={`inline-flex items-center gap-1.5 rounded-md px-2.5 py-1.5 text-xs font-medium transition ${
                viewMode === 'events' ? 'bg-accent-soft text-ink' : 'text-ink-muted hover:text-ink'
              }`}
            >
              <ListIcon className="h-3.5 w-3.5" strokeWidth={1.75} />
              Events
            </button>
            <button
              type="button"
              role="tab"
              aria-selected={viewMode === 'raw'}
              data-testid="settings-logs-tab-raw"
              onClick={() => onViewModeChange('raw')}
              className={`inline-flex items-center gap-1.5 rounded-md px-2.5 py-1.5 text-xs font-medium transition ${
                viewMode === 'raw' ? 'bg-accent-soft text-ink' : 'text-ink-muted hover:text-ink'
              }`}
            >
              <Braces className="h-3.5 w-3.5" strokeWidth={1.75} />
              Raw JSON
            </button>
          </div>

          <div className="max-h-[32rem] overflow-auto p-4">
            {loading ? (
              <p className="text-xs italic text-ink-subtle">Loading trace...</p>
            ) : error ? (
              <p className="text-sm text-rose-500" data-testid="settings-logs-trace-error">{error}</p>
            ) : trace ? (
              viewMode === 'events' ? <TraceEventList trace={trace} /> : <RawTraceView trace={trace} />
            ) : (
              <p className="text-xs italic text-ink-subtle">No events.</p>
            )}
          </div>
        </>
      ) : (
        <p className="p-4 text-sm italic text-ink-subtle">No trace selected.</p>
      )}
    </div>
  );
}

function TraceEventList({ trace }: { trace: TurnTraceResponse }) {
  if (trace.events.length === 0) {
    return <p className="text-xs italic text-ink-subtle">Trace file is empty.</p>;
  }
  return (
    <ol className="space-y-2" data-testid="settings-logs-trace-events">
      {trace.events.map((traceEvent, eventIndex) => {
        const eventRecord = asRecord(traceEvent);
        const type = readString(eventRecord, 'type') ?? 'event';
        const timestamp = readString(eventRecord, 'timestamp');
        const payload = asRecord(eventRecord?.payload);
        const summary = summarizeTraceEvent(type, payload);
        const detailRows = traceEventDetails(type, payload);
        return (
          <li key={`${type}-${eventIndex}`} className="rounded-lg border border-line/60 bg-canvas-raised/70 p-3">
            <div className="flex flex-wrap items-start justify-between gap-2">
              <div className="min-w-0">
                <div className="text-sm font-medium text-ink">{formatTraceEventTitle(type)}</div>
                <div className="mt-0.5 font-mono text-[11px] lowercase text-ink-subtle">{type}</div>
              </div>
              <div className="flex shrink-0 items-center gap-2 text-[11px] text-ink-subtle">
                <span>#{eventIndex + 1}</span>
                {timestamp ? <span>{formatAbsoluteTime(timestamp)}</span> : null}
              </div>
            </div>
            {summary ? (
              <p className="mt-2 whitespace-pre-wrap break-words text-[12px] leading-5 text-ink-muted">
                {summary}
              </p>
            ) : null}
            {detailRows.length > 0 ? (
              <dl className="mt-3 grid gap-2 sm:grid-cols-2">
                {detailRows.map((detail) => (
                  <div key={detail.label} className="min-w-0 rounded-md border border-line/60 bg-canvas-sunken/40 px-2.5 py-2">
                    <dt className="text-[10px] font-medium uppercase tracking-[0.08em] text-ink-subtle">{detail.label}</dt>
                    <dd className="mt-1 truncate font-mono text-[11px] text-ink">{detail.value}</dd>
                  </div>
                ))}
              </dl>
            ) : null}
          </li>
        );
      })}
    </ol>
  );
}

function RawTraceView({ trace }: { trace: TurnTraceResponse }) {
  return (
    <pre className="max-h-[30rem] overflow-auto whitespace-pre-wrap break-words rounded-lg border border-line/60 bg-canvas-sunken/40 p-3 text-[11px] leading-5 text-ink-muted" data-testid="settings-logs-raw-json">
      {trace.events.map((traceEvent) => JSON.stringify(traceEvent, null, 2)).join('\n')}
    </pre>
  );
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function readString(record: Record<string, unknown> | null, key: string): string | null {
  const value = record?.[key];
  return typeof value === 'string' ? value : null;
}

function readNumber(record: Record<string, unknown> | null, key: string): number | null {
  const value = record?.[key];
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function readBoolean(record: Record<string, unknown> | null, key: string): boolean | null {
  const value = record?.[key];
  return typeof value === 'boolean' ? value : null;
}

function summarizeTraceEvent(type: string, payload: Record<string, unknown> | null): string {
  switch (type) {
    case 'chat.user.message':
      return truncateText(readString(payload, 'text') ?? 'User message appended.', 700);
    case 'chat.turn.start':
      return `Assistant turn started for thread ${readString(payload, 'threadId') ?? 'unknown'}.`;
    case 'chat.footman.decision': {
      const nextState = readString(payload, 'nextState') ?? 'unknown';
      const confidence = readNumber(payload, 'confidence');
      const toolsKept = readNumber(payload, 'toolsKept');
      const toolsTotal = readNumber(payload, 'toolsTotal');
      const reasonCode = readString(payload, 'reasonCode');
      const confidenceText = confidence === null ? '' : ` at ${Math.round(confidence * 100)}% confidence`;
      const toolText = toolsKept === null || toolsTotal === null ? '' : ` Kept ${toolsKept} of ${toolsTotal} tools.`;
      const reasonText = reasonCode ? ` Reason: ${reasonCode}.` : '';
      return `Gatekeeper selected ${nextState}${confidenceText}.${toolText}${reasonText}`;
    }
    case 'chat.memory.recalled': {
      const factsCount = readNumber(payload, 'factsCount') ?? 0;
      const eventsCount = readNumber(payload, 'eventsCount') ?? 0;
      const chunksCount = readNumber(payload, 'chunksCount') ?? 0;
      const nuggetsCount = readNumber(payload, 'nuggetsCount') ?? 0;
      const durationMs = readNumber(payload, 'durationMs');
      const preview = readString(payload, 'preview');
      const total = factsCount + eventsCount + chunksCount + nuggetsCount;
      const durationText = durationMs === null ? '' : ` in ${durationMs} ms`;
      const previewText = preview ? ` ${truncateText(preview, 500)}` : '';
      return `Recalled ${total} memory items${durationText}.${previewText}`;
    }
    case 'chat.tool.started': {
      const tool = readString(payload, 'tool') ?? 'tool';
      const group = readString(payload, 'group');
      const argsPreview = readString(payload, 'argsPreview');
      const groupText = group ? ` (${group})` : '';
      const argsText = argsPreview ? ` Args: ${truncateText(argsPreview, 500)}` : '';
      return `Started ${tool}${groupText}.${argsText}`;
    }
    case 'chat.tool.completed': {
      const tool = readString(payload, 'tool') ?? 'tool';
      const ok = readBoolean(payload, 'ok');
      const durationMs = readNumber(payload, 'durationMs');
      const resultSnippet = readString(payload, 'resultSnippet');
      const error = readString(payload, 'error');
      const statusText = ok === false ? 'failed' : 'completed';
      const durationText = durationMs === null ? '' : ` in ${durationMs} ms`;
      const detailText = error ?? resultSnippet;
      return `${tool} ${statusText}${durationText}.${detailText ? ` ${truncateText(detailText, 600)}` : ''}`;
    }
    case 'chat.turn.complete': {
      const cancelled = readBoolean(payload, 'cancelled') === true;
      const finalText = readString(payload, 'finalText');
      if (cancelled) return 'Assistant turn was cancelled.';
      return finalText ? truncateText(finalText, 900) : 'Assistant turn completed.';
    }
    default:
      return truncateText(JSON.stringify(payload ?? {}, null, 2), 700);
  }
}

function traceEventDetails(type: string, payload: Record<string, unknown> | null): Array<{ label: string; value: string }> {
  const rows: Array<{ label: string; value: string }> = [];
  addDetail(rows, 'thread', readString(payload, 'threadId'));
  addDetail(rows, 'message', readString(payload, 'messageId'));

  if (type === 'chat.tool.started' || type === 'chat.tool.completed') {
    addDetail(rows, 'tool', readString(payload, 'tool'));
    addDetail(rows, 'group', readString(payload, 'group'));
    addDetail(rows, 'activity', readString(payload, 'activityId'));
    const durationMs = readNumber(payload, 'durationMs');
    addDetail(rows, 'duration', durationMs === null ? null : `${durationMs} ms`);
    const ok = readBoolean(payload, 'ok');
    addDetail(rows, 'status', ok === null ? null : ok ? 'ok' : 'failed');
  }

  if (type === 'chat.footman.decision') {
    addDetail(rows, 'next state', readString(payload, 'nextState'));
    const confidence = readNumber(payload, 'confidence');
    addDetail(rows, 'confidence', confidence === null ? null : `${Math.round(confidence * 100)}%`);
    const toolsKept = readNumber(payload, 'toolsKept');
    const toolsTotal = readNumber(payload, 'toolsTotal');
    addDetail(rows, 'tools', toolsKept === null || toolsTotal === null ? null : `${toolsKept}/${toolsTotal}`);
    addDetail(rows, 'reason', readString(payload, 'reasonCode'));
  }

  if (type === 'chat.memory.recalled') {
    const factsCount = readNumber(payload, 'factsCount') ?? 0;
    const eventsCount = readNumber(payload, 'eventsCount') ?? 0;
    const chunksCount = readNumber(payload, 'chunksCount') ?? 0;
    const nuggetsCount = readNumber(payload, 'nuggetsCount') ?? 0;
    addDetail(rows, 'facts', String(factsCount));
    addDetail(rows, 'events', String(eventsCount));
    addDetail(rows, 'chunks', String(chunksCount));
    addDetail(rows, 'nuggets', String(nuggetsCount));
  }

  return rows;
}

function addDetail(rows: Array<{ label: string; value: string }>, label: string, value: string | null | undefined) {
  if (!value) return;
  rows.push({ label, value });
}

function formatTraceEventTitle(type: string): string {
  const withoutPrefix = type.startsWith('chat.') ? type.slice(5) : type;
  return withoutPrefix
    .split('.')
    .filter(Boolean)
    .map((segment) => segment.charAt(0).toUpperCase() + segment.slice(1))
    .join(' ');
}

function truncateText(value: string, maxLength: number): string {
  if (value.length <= maxLength) return value;
  return `${value.slice(0, Math.max(0, maxLength - 3))}...`;
}
