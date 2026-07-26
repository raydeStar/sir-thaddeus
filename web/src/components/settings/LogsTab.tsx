import { useEffect, useState } from 'react';
import {
  Check,
  Copy,
  List as ListIcon,
  RefreshCw,
  ShieldCheck,
  Terminal,
} from 'lucide-react';
import {
  getDiagnostics,
  getAssistantInsights,
  getRuntimeLog,
  getTurnTrace,
  listRuntimeLogs,
  listAuditEvents,
  listTurnTraces,
} from '../../lib/activityApi';
import type {
  DiagnosticsResponse,
  AssistantInsightsResponse,
  AuditEvent,
  RuntimeLogResponse,
  RuntimeLogSummary,
  TurnTraceResponse,
  TurnTraceSummary,
} from '@thaddeus/shared-types';
import { SettingsSection as Section } from './SettingsSection';
import { RuntimeLogsPane } from './diagnostics/RuntimeLogsPane';
import { TurnTracePane, type TraceViewMode } from './diagnostics/TurnTracePane';
import { AuditInsightsPane } from './diagnostics/AuditInsightsPane';

type LogPaneId = 'traces' | 'runtime' | 'audit';

// ───────────────────────── Logs ─────────────────────────

export function LogsTab() {
  const [diag, setDiag] = useState<DiagnosticsResponse | null>(null);
  const [diagnosticsError, setDiagnosticsError] = useState<string | null>(null);
  const [traces, setTraces] = useState<TurnTraceSummary[] | null>(null);
  const [traceListError, setTraceListError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);
  const [activeLogPane, setActiveLogPane] = useState<LogPaneId>('traces');
  const [selectedMessageId, setSelectedMessageId] = useState<string | null>(null);
  const [openTrace, setOpenTrace] = useState<TurnTraceResponse | null>(null);
  const [openLoading, setOpenLoading] = useState(false);
  const [traceError, setTraceError] = useState<string | null>(null);
  const [traceViewMode, setTraceViewMode] = useState<TraceViewMode>('events');
  const [runtimeLogs, setRuntimeLogs] = useState<RuntimeLogSummary[] | null>(null);
  const [runtimeLogListError, setRuntimeLogListError] = useState<string | null>(null);
  const [selectedRuntimeLog, setSelectedRuntimeLog] = useState<string | null>(null);
  const [runtimeLog, setRuntimeLog] = useState<RuntimeLogResponse | null>(null);
  const [runtimeLogLoading, setRuntimeLogLoading] = useState(false);
  const [runtimeLogError, setRuntimeLogError] = useState<string | null>(null);
  const [insights, setInsights] = useState<AssistantInsightsResponse | null>(null);
  const [auditEvents, setAuditEvents] = useState<AuditEvent[] | null>(null);
  const [auditError, setAuditError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setDiagnosticsError(null);
    getDiagnostics()
      .then((diagnostics) => {
        if (cancelled) return;
        setDiag(diagnostics);
      })
      .catch((e: Error) => {
        if (!cancelled) setDiagnosticsError(e.message);
      });
    return () => {
      cancelled = true;
    };
  }, [tick]);

  useEffect(() => {
    let cancelled = false;
    setTraceListError(null);
    listTurnTraces(50)
      .then((traceSummaries) => {
        if (cancelled) return;
        setTraces(traceSummaries);
        setSelectedMessageId((currentMessageId) => {
          if (traceSummaries.length === 0) return null;
          if (currentMessageId && traceSummaries.some((traceSummary) => traceSummary.messageId === currentMessageId)) {
            return currentMessageId;
          }
          return traceSummaries[0].messageId;
        });
      })
      .catch((e: Error) => {
        if (!cancelled) setTraceListError(e.message);
      });
    return () => {
      cancelled = true;
    };
  }, [tick]);

  useEffect(() => {
    if (!selectedMessageId) {
      setOpenTrace(null);
      setOpenLoading(false);
      setTraceError(null);
      return;
    }

    let cancelled = false;
    setOpenTrace(null);
    setOpenLoading(true);
    setTraceError(null);
    getTurnTrace(selectedMessageId)
      .then((trace) => {
        if (!cancelled) setOpenTrace(trace);
      })
      .catch((e: Error) => {
        if (!cancelled) setTraceError(e.message);
      })
      .finally(() => {
        if (!cancelled) setOpenLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [selectedMessageId, tick]);

  useEffect(() => {
    let cancelled = false;
    setRuntimeLogListError(null);
    listRuntimeLogs(25)
      .then((logSummaries) => {
        if (cancelled) return;
        setRuntimeLogs(logSummaries);
        setSelectedRuntimeLog((currentFileName) => {
          if (logSummaries.length === 0) return null;
          if (currentFileName && logSummaries.some((logSummary) => logSummary.fileName === currentFileName)) {
            return currentFileName;
          }
          return logSummaries[0].fileName;
        });
      })
      .catch((e: Error) => {
        if (!cancelled) setRuntimeLogListError(e.message);
      });
    return () => {
      cancelled = true;
    };
  }, [tick]);

  useEffect(() => {
    if (!selectedRuntimeLog) {
      setRuntimeLog(null);
      setRuntimeLogLoading(false);
      setRuntimeLogError(null);
      return;
    }

    let cancelled = false;
    setRuntimeLog(null);
    setRuntimeLogLoading(true);
    setRuntimeLogError(null);
    getRuntimeLog(selectedRuntimeLog, 500)
      .then((logFile) => {
        if (!cancelled) setRuntimeLog(logFile);
      })
      .catch((e: Error) => {
        if (!cancelled) setRuntimeLogError(e.message);
      })
      .finally(() => {
        if (!cancelled) setRuntimeLogLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [selectedRuntimeLog, tick]);

  useEffect(() => {
    let cancelled = false;
    setAuditError(null);
    Promise.all([getAssistantInsights(), listAuditEvents(200)])
      .then(([nextInsights, nextEvents]) => {
        if (cancelled) return;
        setInsights(nextInsights);
        setAuditEvents(nextEvents);
      })
      .catch((e: Error) => {
        if (!cancelled) setAuditError(e.message);
      });
    return () => {
      cancelled = true;
    };
  }, [tick]);

  const selectedTraceSummary = traces?.find((traceSummary) => traceSummary.messageId === selectedMessageId) ?? null;
  const selectedRuntimeLogSummary = runtimeLogs?.find((logSummary) => logSummary.fileName === selectedRuntimeLog) ?? null;

  return (
    <div className="space-y-6" role="tabpanel" aria-labelledby="settings-tab-logs">
      <Section
        title="What is logged"
        description="Every chat turn writes a JSON-line trace file under the turn-traces directory below — one file per assistant reply, keyed by its message id. The trace captures the gatekeeper decision, every tool call, search-provider diagnostics, and the final assembled text. If a response feels wrong, open its trace here to see exactly how the runtime got there."
      >
        <div className="grid gap-3" data-testid="settings-logs-paths">
          <PathRow label="Turn traces" value={diag?.turnsRoot ?? ''} testId="logs-path-turns" />
          <PathRow label="Chat threads" value={diag?.threadStoreRoot ?? ''} testId="logs-path-threads" />
          <PathRow label="Runtime logs" value={diag?.logsRoot ?? ''} testId="logs-path-logs" />
        </div>
        {diagnosticsError ? (
          <p className="text-sm text-rose-500" data-testid="settings-logs-diagnostics-error">
            {diagnosticsError}
          </p>
        ) : null}
      </Section>

      <Section
        title="Log viewer"
        description="Read recent turn traces and runtime log files from Settings."
      >
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div
            role="tablist"
            aria-label="Log viewer type"
            className="inline-flex rounded-lg border border-line bg-canvas-raised p-1"
          >
            <button
              type="button"
              role="tab"
              aria-selected={activeLogPane === 'traces'}
              data-testid="settings-logs-pane-traces"
              onClick={() => setActiveLogPane('traces')}
              className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-xs font-medium transition ${
                activeLogPane === 'traces'
                  ? 'bg-accent-soft text-ink shadow-soft'
                  : 'text-ink-muted hover:text-ink'
              }`}
            >
              <ListIcon className="h-3.5 w-3.5" strokeWidth={1.75} />
              Turn traces
            </button>
            <button
              type="button"
              role="tab"
              aria-selected={activeLogPane === 'runtime'}
              data-testid="settings-logs-pane-runtime"
              onClick={() => setActiveLogPane('runtime')}
              className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-xs font-medium transition ${
                activeLogPane === 'runtime'
                  ? 'bg-accent-soft text-ink shadow-soft'
                  : 'text-ink-muted hover:text-ink'
              }`}
            >
              <Terminal className="h-3.5 w-3.5" strokeWidth={1.75} />
              Runtime logs
            </button>
            <button
              type="button"
              role="tab"
              aria-selected={activeLogPane === 'audit'}
              data-testid="settings-logs-pane-audit"
              onClick={() => setActiveLogPane('audit')}
              className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-xs font-medium transition ${
                activeLogPane === 'audit'
                  ? 'bg-accent-soft text-ink shadow-soft'
                  : 'text-ink-muted hover:text-ink'
              }`}
            >
              <ShieldCheck className="h-3.5 w-3.5" strokeWidth={1.75} />
              Audit &amp; insights
            </button>
          </div>
          <button
            type="button"
            data-testid="settings-logs-refresh"
            onClick={() => setTick((n) => n + 1)}
            className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3 py-1.5 text-xs font-medium text-ink-muted transition hover:border-line-strong hover:text-ink"
          >
            <RefreshCw className="h-3.5 w-3.5" strokeWidth={1.75} />
            Refresh
          </button>
        </div>

        {activeLogPane === 'traces' ? (
          <TurnTracePane
            traces={traces}
            error={traceListError}
            selectedMessageId={selectedMessageId}
            selectedSummary={selectedTraceSummary}
            trace={openTrace}
            loading={openLoading}
            traceError={traceError}
            viewMode={traceViewMode}
            onViewModeChange={setTraceViewMode}
            onSelect={(messageId) => {
              setTraceViewMode('events');
              setSelectedMessageId(messageId);
            }}
          />
        ) : activeLogPane === 'runtime' ? (
          <RuntimeLogsPane
            logs={runtimeLogs}
            error={runtimeLogListError}
            selectedFileName={selectedRuntimeLog}
            selectedSummary={selectedRuntimeLogSummary}
            log={runtimeLog}
            loading={runtimeLogLoading}
            logError={runtimeLogError}
            onSelect={setSelectedRuntimeLog}
          />
        ) : (
          <AuditInsightsPane
            insights={insights}
            events={auditEvents}
            error={auditError}
          />
        )}
      </Section>
    </div>
  );
}

function PathRow({ label, value, testId }: { label: string; value: string; testId: string }) {
  const [copied, setCopied] = useState(false);
  const onCopy = async () => {
    if (!value) return;
    try {
      await navigator.clipboard?.writeText(value);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard not available — leave the path visible for manual copy */
    }
  };
  return (
    <div className="flex items-center justify-between gap-3 rounded-xl border border-line bg-canvas-raised/60 px-3 py-2">
      <div className="min-w-0 flex-1">
        <div className="text-[11px] uppercase tracking-[0.08em] text-ink-subtle">{label}</div>
        <div data-testid={testId} className="truncate font-mono text-[12px] text-ink">
          {value || '—'}
        </div>
      </div>
      {value ? (
        <button
          type="button"
          onClick={onCopy}
          aria-label={`Copy ${label} path`}
          className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-full border border-transparent text-ink-muted transition hover:border-line hover:bg-canvas-raised hover:text-ink"
        >
          {copied ? <Check className="h-3.5 w-3.5" strokeWidth={2} /> : <Copy className="h-3.5 w-3.5" strokeWidth={1.75} />}
        </button>
      ) : null}
    </div>
  );
}
