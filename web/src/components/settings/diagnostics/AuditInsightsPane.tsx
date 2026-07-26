import { useMemo, useState } from 'react';
import { Download, Search } from 'lucide-react';
import type {
  AssistantInsightsResponse,
  AuditEvent,
} from '@thaddeus/shared-types';
import { exportAuditTrail } from '../../../lib/activityApi';

export function AuditInsightsPane({
  insights,
  events,
  error,
}: {
  insights: AssistantInsightsResponse | null;
  events: AuditEvent[] | null;
  error: string | null;
}) {
  const [query, setQuery] = useState('');
  const [exportError, setExportError] = useState<string | null>(null);
  const filteredEvents = useMemo(() => {
    const normalized = query.trim().toLowerCase();
    if (!normalized) return events ?? [];
    return (events ?? []).filter((event) =>
      [event.actor, event.action, event.result, event.target ?? '']
        .some((value) => value.toLowerCase().includes(normalized)));
  }, [events, query]);

  const onExport = async () => {
    setExportError(null);
    try {
      const blob = await exportAuditTrail();
      const href = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = href;
      anchor.download = `sir-thaddeus-audit-${new Date().toISOString().slice(0, 10)}.jsonl`;
      anchor.click();
      URL.revokeObjectURL(href);
    } catch (exportFailure) {
      setExportError(exportFailure instanceof Error ? exportFailure.message : 'Audit export failed');
    }
  };

  if (error) {
    return <p className="mt-4 text-sm text-rose-500" role="alert">{error}</p>;
  }

  return (
    <div className="mt-4 space-y-4" data-testid="settings-audit-insights">
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {(insights?.metrics ?? []).map((metric) => (
          <article
            key={metric.key}
            className="rounded-xl border border-line bg-canvas-raised/60 p-3"
            data-testid={`insight-${metric.key}`}
          >
            <div className="flex items-start justify-between gap-2">
              <h4 className="text-xs font-semibold text-ink">{metric.label}</h4>
              <span className={`rounded-full px-2 py-0.5 text-[10px] ${
                metric.status === 'measured'
                  ? 'bg-emerald-500/10 text-emerald-600'
                  : 'bg-amber-500/10 text-amber-600'
              }`}>
                {metric.status === 'measured' ? `${Math.round((metric.value ?? 0) * 100)}%` : 'Needs evidence'}
              </span>
            </div>
            <p className="mt-2 text-[11px] leading-4 text-ink-muted">{metric.definition}</p>
            {metric.status === 'measured' ? (
              <p className="mt-2 font-mono text-[10px] text-ink-subtle">
                {metric.numerator} / {metric.denominator}
              </p>
            ) : null}
          </article>
        ))}
      </div>

      <div className="overflow-hidden rounded-xl border border-line">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-line bg-canvas-raised px-3 py-2">
          <div>
            <h4 className="text-xs font-semibold text-ink">Recent audit trail</h4>
            <p className="text-[10px] text-ink-subtle">
              Local, append-only evidence · {insights?.sampleEvents ?? 0} sampled events
            </p>
          </div>
          <div className="flex items-center gap-2">
            <label className="relative">
              <span className="sr-only">Filter audit trail</span>
              <Search className="pointer-events-none absolute left-2.5 top-2 h-3.5 w-3.5 text-ink-subtle" />
              <input
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder="Filter actor, action, result…"
                data-testid="audit-filter"
                className="h-8 w-56 rounded-full border border-line bg-canvas pl-8 pr-3 text-xs text-ink outline-none focus:border-accent"
              />
            </label>
            <button
              type="button"
              onClick={onExport}
              data-testid="audit-export"
              className="inline-flex h-8 items-center gap-1.5 rounded-full border border-line bg-canvas px-3 text-xs font-medium text-ink-muted hover:text-ink"
            >
              <Download className="h-3.5 w-3.5" />
              Export JSONL
            </button>
          </div>
        </div>
        {exportError ? <p className="px-3 pt-2 text-xs text-rose-500">{exportError}</p> : null}
        <div className="max-h-[28rem] overflow-auto">
          {filteredEvents.length === 0 ? (
            <p className="p-4 text-sm text-ink-muted">
              {(events ?? []).length === 0 ? 'No audit events recorded yet.' : 'No audit events match this filter.'}
            </p>
          ) : (
            <table className="w-full text-left text-[11px]">
              <thead className="sticky top-0 bg-canvas-raised text-ink-subtle">
                <tr>
                  <th className="px-3 py-2 font-medium">Time</th>
                  <th className="px-3 py-2 font-medium">Actor</th>
                  <th className="px-3 py-2 font-medium">Action</th>
                  <th className="px-3 py-2 font-medium">Result</th>
                  <th className="px-3 py-2 font-medium">Target</th>
                </tr>
              </thead>
              <tbody>
                {filteredEvents.map((event, index) => (
                  <tr key={`${event.timestamp}-${event.action}-${index}`} className="border-t border-line/70">
                    <td className="whitespace-nowrap px-3 py-2 font-mono text-ink-subtle">
                      {new Date(event.timestamp).toLocaleTimeString()}
                    </td>
                    <td className="px-3 py-2 text-ink-muted">{event.actor}</td>
                    <td className="px-3 py-2 font-mono text-ink">{event.action}</td>
                    <td className="px-3 py-2">
                      <span className={event.result === 'ok' ? 'text-emerald-600' : 'text-amber-600'}>
                        {event.result}
                      </span>
                    </td>
                    <td className="max-w-56 truncate px-3 py-2 font-mono text-ink-muted">
                      {event.target ?? '—'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </div>
  );
}
