import { createFileRoute } from '@tanstack/react-router';
import { Activity, ArrowRight, Database, HeartPulse, RefreshCw } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import {
  getModule,
  invokeModuleTool,
  listModules,
  type ModuleDetail,
  type ModuleInvokeResponse,
  type ModuleSummary,
} from '../lib/modulesApi';

export const Route = createFileRoute('/modules')({
  component: DataRoute,
});

const healthPackId = 'com.thaddeus.health';

function DataRoute() {
  const [modules, setModules] = useState<ModuleSummary[]>([]);
  const [healthDetail, setHealthDetail] = useState<ModuleDetail | null>(null);
  const [latestResult, setLatestResult] = useState<ModuleInvokeResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function refresh() {
    const rows = await listModules();
    setModules(rows);
    const health = rows.find((module) => module.id === healthPackId);
    setHealthDetail(health ? await getModule(health.id) : null);
  }

  useEffect(() => {
    let disposed = false;
    setLoading(true);
    refresh()
      .catch((err) => {
        if (!disposed) setError(err instanceof Error ? err.message : String(err));
      })
      .finally(() => {
        if (!disposed) setLoading(false);
      });
    return () => {
      disposed = true;
    };
  }, []);

  async function runHealthTool(label: string, toolName: string, args?: Record<string, unknown>) {
    if (!healthDetail) return;
    setBusy(label);
    setError(null);
    try {
      const result = await invokeModuleTool(healthDetail.id, toolName, args);
      setLatestResult(result);
      await refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(null);
    }
  }

  return (
    <PageScaffold
      testId="route-data"
      title="Data"
      subtitle="Health, personal signals, and module-provided records."
      width="wide"
    >
      {error ? (
        <p className="mb-4 rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
          {error}
        </p>
      ) : null}

      {loading ? (
        <p className="text-sm text-ink-muted">Loading data sources...</p>
      ) : (
        <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_320px]">
          <HealthDataPanel
            detail={healthDetail}
            latestResult={latestResult}
            busy={busy}
            onRun={runHealthTool}
          />
          <DataSourcesPanel modules={modules} />
        </div>
      )}
    </PageScaffold>
  );
}

function HealthDataPanel({
  detail,
  latestResult,
  busy,
  onRun,
}: {
  detail: ModuleDetail | null;
  latestResult: ModuleInvokeResponse | null;
  busy: string | null;
  onRun: (label: string, toolName: string, args?: Record<string, unknown>) => Promise<void>;
}) {
  const summary = useMemo(
    () => summarizeHealthResult(latestResult?.json as Record<string, unknown> | undefined),
    [latestResult],
  );
  const unavailable = !detail;
  const needsSetup = Boolean(detail && detail.approvalStatus !== 'Approved');
  const disabled = Boolean(busy || unavailable || needsSetup || detail?.disabled);

  return (
    <section className="min-w-0 rounded-md border border-line bg-canvas p-5" data-testid="data-health-panel">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <HeartPulse className="h-5 w-5 text-accent" strokeWidth={1.8} />
            <h2 className="text-lg font-semibold text-ink">Health</h2>
          </div>
          <p className="mt-1 text-sm text-ink-muted">
            {detail?.description ?? 'Health Pack is not installed.'}
          </p>
        </div>
        <StatusPill
          label={
            unavailable
              ? 'missing'
              : detail?.disabled
                ? 'disabled'
                : needsSetup
                  ? 'setup'
                  : detail.status
          }
        />
      </div>

      {needsSetup || detail?.disabled ? (
        <a
          href="/settings#modules"
          className="mt-4 inline-flex items-center gap-2 rounded-md border border-line px-3 py-2 text-sm font-medium text-ink transition hover:border-accent hover:text-accent"
        >
          Open module setup
          <ArrowRight className="h-4 w-4" strokeWidth={1.8} />
        </a>
      ) : null}

      <div className="mt-5 flex flex-wrap gap-2">
        <ActionButton
          disabled={disabled}
          busy={busy === 'status'}
          onClick={() => onRun('status', 'health.provider_status')}
        >
          Provider Status
        </ActionButton>
        <ActionButton
          disabled={disabled}
          busy={busy === 'backfill'}
          onClick={() => onRun('backfill', 'health.backfill', { days: 30 })}
        >
          Backfill 30 Days
        </ActionButton>
        <ActionButton
          disabled={disabled}
          busy={busy === 'brief'}
          onClick={() => onRun('brief', 'health.get_morning_strategy_brief')}
        >
          Morning Brief
        </ActionButton>
      </div>

      <div className="mt-6 grid gap-4 lg:grid-cols-2">
        {summary.length > 0 ? (
          <dl className="grid gap-3">
            {summary.map((item) => (
              <KeyValue key={item.label} label={item.label} value={item.value} />
            ))}
          </dl>
        ) : (
          <div className="rounded-md border border-line/70 bg-canvas-sunken/30 p-4">
            <p className="text-sm text-ink-muted">
              Health status and briefs will appear here after the Health Pack is approved and connected.
            </p>
          </div>
        )}

        <LatestResult result={latestResult} />
      </div>
    </section>
  );
}

function DataSourcesPanel({ modules }: { modules: ModuleSummary[] }) {
  return (
    <aside className="rounded-md border border-line bg-canvas p-5" data-testid="data-sources-panel">
      <div className="flex items-center gap-2">
        <Database className="h-4 w-4 text-ink-muted" strokeWidth={1.8} />
        <h2 className="text-sm font-semibold uppercase tracking-wide text-ink-muted">Sources</h2>
      </div>
      {modules.length === 0 ? (
        <p className="mt-4 text-sm text-ink-muted">No module-backed data sources found.</p>
      ) : (
        <ul className="mt-4 divide-y divide-line">
          {modules.map((module) => (
            <li key={module.id} className="py-3">
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <p className="truncate text-sm font-medium text-ink">{module.name}</p>
                  <p className="mt-0.5 text-xs text-ink-muted">v{module.version}</p>
                </div>
                <StatusPill label={module.status} />
              </div>
              <p className="mt-2 text-xs text-ink-muted">
                {module.toolCount} tools · {module.permissionCount} permissions
              </p>
            </li>
          ))}
        </ul>
      )}
    </aside>
  );
}

function ActionButton({
  children,
  disabled,
  busy,
  onClick,
}: {
  children: string;
  disabled?: boolean;
  busy?: boolean;
  onClick: () => void | Promise<void>;
}) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={() => void onClick()}
      className="inline-flex h-9 items-center gap-2 rounded-md border border-line px-3 text-sm font-medium text-ink transition hover:border-accent hover:text-accent disabled:cursor-not-allowed disabled:opacity-50"
    >
      {busy ? <RefreshCw className="h-4 w-4 animate-spin" strokeWidth={1.8} /> : null}
      {children}
    </button>
  );
}

function LatestResult({ result }: { result: ModuleInvokeResponse | null }) {
  if (!result) {
    return (
      <div className="rounded-md border border-line/70 bg-canvas-sunken/30 p-4">
        <div className="flex items-center gap-2 text-sm font-medium text-ink">
          <Activity className="h-4 w-4 text-ink-muted" strokeWidth={1.8} />
          Latest Output
        </div>
        <p className="mt-2 text-sm text-ink-muted">No health action has run in this session.</p>
      </div>
    );
  }

  const brief = formatBrief(result.json as Record<string, unknown> | undefined);
  return (
    <div className="rounded-md border border-line/70 bg-canvas-sunken/30 p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm font-medium text-ink">Latest Output</p>
        <p className="text-xs text-ink-muted">{formatTime(result.invokedAt)}</p>
      </div>
      <p className="mt-1 font-mono text-xs text-ink-muted">{result.toolName}</p>
      {brief.length > 0 ? (
        <ul className="mt-3 space-y-2 text-sm text-ink">
          {brief.map((line) => (
            <li key={line}>{line}</li>
          ))}
        </ul>
      ) : (
        <pre className="mt-3 max-h-72 overflow-auto whitespace-pre-wrap rounded bg-ink/[0.03] p-3 text-xs text-ink">
          {JSON.stringify(result.json ?? result.content, null, 2)}
        </pre>
      )}
    </div>
  );
}

function StatusPill({ label }: { label: string }) {
  const tone =
    label === 'approved'
      ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
      : label === 'error'
        ? 'border-rose-200 bg-rose-50 text-rose-700'
        : label === 'disabled' || label === 'missing'
          ? 'border-line bg-ink/[0.03] text-ink-muted'
          : 'border-amber-200 bg-amber-50 text-amber-700';
  return (
    <span className={`shrink-0 rounded-full border px-2 py-0.5 text-[11px] font-medium ${tone}`}>
      {label}
    </span>
  );
}

function KeyValue({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0 rounded-md border border-line/70 bg-canvas-sunken/30 p-3">
      <dt className="text-xs uppercase tracking-wide text-ink-subtle">{label}</dt>
      <dd className="mt-0.5 break-words text-sm text-ink">{value}</dd>
    </div>
  );
}

function summarizeHealthResult(json?: Record<string, unknown>): Array<{ label: string; value: string }> {
  if (!json) return [];
  const rows: Array<{ label: string; value: string }> = [];
  const provider = readNested(json, ['selectedProvider']) || readNested(json, ['providerName']) || readNested(json, ['provider']);
  const lifecycle = readNested(json, ['lifecycle']) || readNested(json, ['status', 'lifecycle']);
  const connected = readNested(json, ['connected']) || readNested(json, ['status', 'connected']);
  const readiness = readNested(json, ['readinessLevel']);
  const snapshots =
    readNested(json, ['sync', 'snapshotCount']) ||
    readNested(json, ['status', 'sync', 'snapshotCount']) ||
    readNested(json, ['snapshotsStored']) ||
    readNested(json, ['snapshotCount']);
  const warnings =
    readNested(json, ['sync', 'warnings']) ||
    readNested(json, ['status', 'sync', 'warnings']) ||
    readNested(json, ['warnings']);
  const caveats = Array.isArray(json.caveats) ? json.caveats.join('; ') : readNested(json, ['dataQuality', 'caveats']);

  if (provider) rows.push({ label: 'Provider', value: provider });
  if (lifecycle) rows.push({ label: 'Status', value: lifecycle });
  if (connected) rows.push({ label: 'Connected', value: connected });
  if (readiness) rows.push({ label: 'Brief', value: readiness });
  if (snapshots) rows.push({ label: 'Stored Snapshots', value: snapshots });
  if (warnings) rows.push({ label: 'Warnings', value: warnings });
  if (caveats) rows.push({ label: 'Data Quality', value: caveats });
  return rows;
}

function formatBrief(json?: Record<string, unknown>): string[] {
  if (!json) return [];
  const lines: string[] = [];
  const headline = readNested(json, ['headline']) || readNested(json, ['brief', 'headline']);
  const summary = readNested(json, ['summary']) || readNested(json, ['brief', 'summary']);
  const strategy = readNested(json, ['strategy']) || readNested(json, ['brief', 'strategy']);
  const caveats = readNested(json, ['caveats']) || readNested(json, ['dataQuality', 'caveats']);
  if (headline) lines.push(headline);
  if (summary) lines.push(summary);
  if (strategy) lines.push(strategy);
  if (caveats) lines.push(`Data quality: ${caveats}`);
  return lines;
}

function readNested(value: Record<string, unknown>, path: string[]): string {
  let current: unknown = value;
  for (const key of path) {
    if (!current || typeof current !== 'object' || !(key in current)) return '';
    current = (current as Record<string, unknown>)[key];
  }
  if (current === null || current === undefined) return '';
  if (Array.isArray(current)) return current.join('; ');
  return typeof current === 'string' ? current : JSON.stringify(current);
}

function formatTime(iso: string): string {
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}
