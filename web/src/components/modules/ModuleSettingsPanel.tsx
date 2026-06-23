import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import {
  approveModule,
  checkModuleStatus,
  denyModule,
  disableModule,
  enableModule,
  getModule,
  invokeModuleTool,
  listModules,
  type ModuleDetail,
  type ModuleInvokeResponse,
  type ModuleSummary,
} from '../../lib/modulesApi';
import { openExternalUrl } from '../../lib/externalLinks';

const healthPackId = 'com.thaddeus.health';

export function ModuleSettingsPanel() {
  const [modules, setModules] = useState<ModuleSummary[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [detail, setDetail] = useState<ModuleDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [latestResult, setLatestResult] = useState<ModuleInvokeResponse | null>(null);

  async function refresh(nextSelectedId = selectedId) {
    setError(null);
    const rows = await listModules();
    setModules(rows);
    const id = nextSelectedId ?? rows[0]?.id ?? null;
    setSelectedId(id);
    setDetail(id ? await getModule(id) : null);
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function runAction(label: string, action: () => Promise<ModuleDetail | ModuleInvokeResponse | unknown>) {
    setBusy(label);
    setError(null);
    try {
      const result = await action();
      if (isInvokeResponse(result)) setLatestResult(result);
      await refresh(selectedId);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(null);
    }
  }

  async function selectModule(id: string) {
    setSelectedId(id);
    setLatestResult(null);
    setError(null);
    try {
      setDetail(await getModule(id));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  return (
    <div data-testid="settings-modules-panel">
      <div className="mb-6">
        <h2 className="text-lg font-semibold text-ink">Module Runtime</h2>
        <p className="mt-1 text-sm text-ink-muted">
          Install, approve, configure, and debug external packs.
        </p>
      </div>
      {error ? (
        <p className="mb-4 rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
          {error}
        </p>
      ) : null}

      {loading ? (
        <p className="text-sm text-ink-muted">Loading modules...</p>
      ) : modules.length === 0 ? (
        <p className="text-sm text-ink-muted">No manifest-backed modules were discovered.</p>
      ) : (
        <div className="grid gap-6 lg:grid-cols-[minmax(280px,360px),1fr]">
          <ModuleList modules={modules} selectedId={selectedId} onSelect={selectModule} />
          {detail ? (
            <ModuleDetailPanel
              detail={detail}
              latestResult={latestResult}
              busy={busy}
              runAction={runAction}
            />
          ) : null}
        </div>
      )}
    </div>
  );
}

function ModuleList({
  modules,
  selectedId,
  onSelect,
}: {
  modules: ModuleSummary[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}) {
  return (
    <ul className="divide-y divide-line rounded-md border border-line bg-canvas">
      {modules.map((module) => (
        <li key={module.id}>
          <button
            type="button"
            onClick={() => void onSelect(module.id)}
            className={`w-full px-4 py-3 text-left transition-colors hover:bg-ink/[0.03] ${
              selectedId === module.id ? 'bg-accent-soft' : ''
            }`}
          >
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <p className="truncate text-sm font-semibold text-ink">{module.name}</p>
                <p className="mt-0.5 text-xs text-ink-muted">v{module.version}</p>
              </div>
              <StatusPill status={module.status} />
            </div>
            <div className="mt-3 grid grid-cols-3 gap-2 text-[11px] text-ink-muted">
              <Metric label="Approval" value={module.approvalStatus} />
              <Metric label="Perms" value={String(module.permissionCount)} />
              <Metric label="Tools" value={String(module.toolCount)} />
            </div>
            <p className="mt-2 truncate text-xs text-ink-muted">
              {module.lastError
                ? `Error: ${module.lastError}`
                : module.lastInvocation
                  ? `Last run ${formatTime(module.lastInvocation)}`
                  : 'No runs yet'}
            </p>
          </button>
        </li>
      ))}
    </ul>
  );
}

function ModuleDetailPanel({
  detail,
  latestResult,
  busy,
  runAction,
}: {
  detail: ModuleDetail;
  latestResult: ModuleInvokeResponse | null;
  busy: string | null;
  runAction: (label: string, action: () => Promise<ModuleDetail | ModuleInvokeResponse | unknown>) => Promise<void>;
}) {
  const isHealth = detail.id === healthPackId;
  const disabled = Boolean(busy);
  return (
    <section className="min-w-0">
      <div className="flex flex-wrap items-start justify-between gap-4 border-b border-line pb-5">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="text-xl font-semibold text-ink">{detail.name}</h2>
            <StatusPill status={detail.status} />
          </div>
          <p className="mt-1 text-sm text-ink-muted">{detail.description}</p>
          <p className="mt-2 truncate font-mono text-[11px] text-ink-muted">{detail.manifestPath}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          {detail.approvalStatus !== 'Approved' ? (
            <ActionButton disabled={disabled} onClick={() => runAction('approve', () => approveModule(detail.id))}>
              Approve
            </ActionButton>
          ) : null}
          {detail.approvalStatus !== 'Denied' ? (
            <ActionButton disabled={disabled} onClick={() => runAction('deny', () => denyModule(detail.id))}>
              Deny
            </ActionButton>
          ) : null}
          {detail.disabled ? (
            <ActionButton disabled={disabled} onClick={() => runAction('enable', () => enableModule(detail.id))}>
              Enable
            </ActionButton>
          ) : (
            <ActionButton disabled={disabled} onClick={() => runAction('disable', () => disableModule(detail.id))}>
              Disable
            </ActionButton>
          )}
          <ActionButton disabled={disabled} onClick={() => runAction('status', () => checkModuleStatus(detail.id))}>
            Check Status
          </ActionButton>
        </div>
      </div>

      <div className="mt-6 grid gap-6 xl:grid-cols-2">
        <InfoSection title="Permissions">
          <JsonBlock value={detail.requestedPermissions ?? {}} />
        </InfoSection>

        <InfoSection title="Runtime">
          <dl className="space-y-2 text-sm">
            <KeyValue label="Approval" value={detail.approvalStatus} />
            <KeyValue label="Last status" value={detail.lastStatusCheck ? formatTime(detail.lastStatusCheck) : 'Never'} />
            <KeyValue label="Last run" value={detail.lastInvocation ? formatTime(detail.lastInvocation) : 'Never'} />
            <KeyValue label="Command" value={detail.execution ? formatCommand(detail.execution) : 'Not configured'} />
            <KeyValue label="Env" value={detail.execution?.envKeys.length ? detail.execution.envKeys.join(', ') : 'None declared'} />
          </dl>
        </InfoSection>
      </div>

      {isHealth ? (
        <HealthPackPanel
          detail={detail}
          busy={busy}
          latestResult={latestResult}
          runAction={runAction}
        />
      ) : null}

      <InfoSection title="Tools" className="mt-6">
        <ul className="divide-y divide-line">
          {detail.tools.map((tool) => (
            <li key={tool.name} className="flex items-center justify-between gap-3 py-2.5">
              <span className="min-w-0 truncate font-mono text-sm text-ink">{tool.name}</span>
              {tool.canInvokeManually ? (
                <ActionButton
                  disabled={disabled}
                  onClick={() => runAction(tool.name, () => invokeModuleTool(detail.id, tool.name))}
                >
                  Invoke
                </ActionButton>
              ) : null}
            </li>
          ))}
        </ul>
      </InfoSection>

      {latestResult ? (
        <InfoSection title="Latest Result" className="mt-6">
          <ResultBlock result={latestResult} />
        </InfoSection>
      ) : null}

      <InfoSection title="Recent Audit" className="mt-6">
        {detail.recentAuditEvents.length === 0 ? (
          <p className="text-sm text-ink-muted">No module audit events yet.</p>
        ) : (
          <ul className="divide-y divide-line">
            {detail.recentAuditEvents.map((event) => (
              <li key={event.id} className="py-2 text-sm">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="font-medium text-ink">{event.action}</span>
                  <span className="text-xs text-ink-muted">{formatTime(event.at)}</span>
                </div>
                <p className="mt-0.5 text-xs text-ink-muted">
                  {event.result}
                  {event.toolName ? ` - ${event.toolName}` : ''}
                  {event.message ? ` - ${event.message}` : ''}
                </p>
              </li>
            ))}
          </ul>
        )}
      </InfoSection>
    </section>
  );
}

function HealthPackPanel({
  detail,
  busy,
  latestResult,
  runAction,
}: {
  detail: ModuleDetail;
  busy: string | null;
  latestResult: ModuleInvokeResponse | null;
  runAction: (label: string, action: () => Promise<ModuleInvokeResponse | unknown>) => Promise<void>;
}) {
  const json = latestResult?.json as Record<string, unknown> | undefined;
  const summary = useMemo(() => summarizeHealthResult(json), [json]);
  const disabled = Boolean(busy);
  const [providerName, setProviderName] = useState('mock');
  const [clientId, setClientId] = useState('');
  const [clientSecret, setClientSecret] = useState('');
  const [redirectUri, setRedirectUri] = useState('http://localhost:8787/oauth/callback');
  const [authCode, setAuthCode] = useState('');
  const [authState, setAuthState] = useState('');
  const [syncStart, setSyncStart] = useState(todayMinus(6));
  const [syncEnd, setSyncEnd] = useState(todayIso());

  return (
    <InfoSection title="Health Pack" className="mt-6">
      <div className="grid gap-4 lg:grid-cols-[minmax(260px,360px),1fr]">
        <div className="space-y-3">
          <label className="block text-xs font-medium uppercase tracking-wide text-ink-subtle">
            Provider
            <select
              value={providerName}
              onChange={(event) => setProviderName(event.target.value)}
              className="mt-1 h-9 w-full rounded-md border border-line bg-canvas px-2 text-sm normal-case tracking-normal text-ink"
            >
              <option value="mock">Mock</option>
              <option value="google-health">Google Health</option>
            </select>
          </label>

          {providerName === 'google-health' ? (
            <div className="space-y-2">
              <TextInput label="Client ID" value={clientId} onChange={setClientId} />
              <TextInput label="Client Secret (optional)" value={clientSecret} onChange={setClientSecret} type="password" />
              <TextInput label="Redirect URI" value={redirectUri} onChange={setRedirectUri} />
              <TextInput label="OAuth Code" value={authCode} onChange={setAuthCode} />
              <TextInput label="OAuth State" value={authState} onChange={setAuthState} />
            </div>
          ) : null}

          <div className="flex flex-wrap gap-2">
            <ActionButton
              disabled={disabled}
              onClick={() =>
                runAction('set-provider', () =>
                  invokeModuleTool(detail.id, 'health.set_provider_config', {
                    selectedProvider: providerName,
                    googleHealth:
                      providerName === 'google-health'
                        ? {
                            clientId,
                            clientSecret,
                            redirectUri,
                          }
                        : undefined,
                  }),
                )
              }
            >
              Save Provider
            </ActionButton>
            <ActionButton disabled={disabled} onClick={() => runAction('start-auth', () => invokeModuleTool(detail.id, 'health.start_provider_auth'))}>
              Connect
            </ActionButton>
            <ActionButton
              disabled={disabled || providerName !== 'google-health' || authCode.trim().length === 0}
              onClick={() =>
                runAction('complete-auth', () =>
                  invokeModuleTool(detail.id, 'health.complete_provider_auth', {
                    code: authCode.trim(),
                    state: authState.trim() || undefined,
                  }),
                )
              }
            >
              Complete Auth
            </ActionButton>
            <ActionButton disabled={disabled} onClick={() => runAction('disconnect', () => invokeModuleTool(detail.id, 'health.disconnect_provider'))}>
              Disconnect
            </ActionButton>
            <ActionButton disabled={disabled} onClick={() => runAction('clear-provider', () => invokeModuleTool(detail.id, 'health.clear_provider_config'))}>
              Reset Provider
            </ActionButton>
          </div>
        </div>

        <div className="space-y-3">
          {summary.length > 0 ? (
            <dl className="grid gap-2 text-sm md:grid-cols-2">
              {summary.map((item) => (
                <KeyValue key={item.label} label={item.label} value={item.value} />
              ))}
            </dl>
          ) : (
            <p className="text-sm text-ink-muted">Run provider status to see lifecycle, credential, sync, and data-quality details.</p>
          )}
          <AuthUrl value={json} />
        </div>
      </div>

      <div className="mt-4 flex flex-wrap gap-2">
        <ActionButton disabled={disabled} onClick={() => runAction('provider', () => invokeModuleTool(detail.id, 'health.provider_status'))}>
          Provider Status
        </ActionButton>
        <ActionButton disabled={disabled} onClick={() => runAction('secret-store', () => invokeModuleTool(detail.id, 'health.secret_store_status'))}>
          Secret Store
        </ActionButton>
        <ActionButton disabled={disabled} onClick={() => runAction('backfill', () => invokeModuleTool(detail.id, 'health.backfill', { days: 30 }))}>
          Backfill 30 Days
        </ActionButton>
        <ActionButton
          disabled={disabled}
          onClick={() =>
            runAction('sync-range', () =>
              invokeModuleTool(detail.id, 'health.sync_range', { startDate: syncStart, endDate: syncEnd }),
            )
          }
        >
          Sync Range
        </ActionButton>
        <ActionButton disabled={disabled} onClick={() => runAction('brief', () => invokeModuleTool(detail.id, 'health.get_morning_strategy_brief'))}>
          Morning Brief
        </ActionButton>
        <ActionButton disabled={disabled} onClick={() => runAction('health-audit', () => invokeModuleTool(detail.id, 'health.provider_audit_events', { limit: 20 }))}>
          Provider Audit
        </ActionButton>
      </div>

      <div className="mt-3 grid gap-2 md:grid-cols-2">
        <TextInput label="Sync start" value={syncStart} onChange={setSyncStart} />
        <TextInput label="Sync end" value={syncEnd} onChange={setSyncEnd} />
      </div>
    </InfoSection>
  );
}

function TextInput({
  label,
  value,
  onChange,
  type = 'text',
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  type?: string;
}) {
  return (
    <label className="block text-xs font-medium uppercase tracking-wide text-ink-subtle">
      {label}
      <input
        type={type}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="mt-1 h-9 w-full rounded-md border border-line bg-canvas px-2 text-sm normal-case tracking-normal text-ink"
      />
    </label>
  );
}

function AuthUrl({ value }: { value?: Record<string, unknown> }) {
  const authUrl = readNested(value ?? {}, ['authUrl']);
  if (!authUrl) return null;
  return (
    <button
      type="button"
      onClick={() => void openExternalUrl(authUrl)}
      className="block max-w-full truncate text-left text-sm font-medium text-accent hover:underline"
    >
      Open authorization URL
    </button>
  );
}

function InfoSection({
  title,
  children,
  className = '',
}: {
  title: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={className}>
      <h3 className="mb-3 text-sm font-semibold uppercase tracking-wide text-ink-muted">{title}</h3>
      <div className="rounded-md border border-line bg-canvas p-4">{children}</div>
    </section>
  );
}

function ActionButton({
  children,
  disabled,
  onClick,
}: {
  children: ReactNode;
  disabled?: boolean;
  onClick: () => void | Promise<void>;
}) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={() => void onClick()}
      className="inline-flex h-8 items-center rounded-md border border-line px-3 text-xs font-medium text-ink transition-colors hover:border-accent hover:text-accent disabled:cursor-not-allowed disabled:opacity-50"
    >
      {children}
    </button>
  );
}

function StatusPill({ status }: { status: string }) {
  const tone =
    status === 'approved'
      ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
      : status === 'error'
        ? 'border-rose-200 bg-rose-50 text-rose-700'
        : status === 'disabled'
          ? 'border-line bg-ink/[0.03] text-ink-muted'
          : 'border-amber-200 bg-amber-50 text-amber-700';
  return (
    <span className={`shrink-0 rounded-full border px-2 py-0.5 text-[11px] font-medium ${tone}`}>
      {status}
    </span>
  );
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <span>
      <span className="block uppercase tracking-wide text-ink-subtle">{label}</span>
      <span className="block truncate text-ink-muted">{value}</span>
    </span>
  );
}

function KeyValue({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs uppercase tracking-wide text-ink-subtle">{label}</dt>
      <dd className="mt-0.5 break-words text-sm text-ink">{value}</dd>
    </div>
  );
}

function JsonBlock({ value }: { value: unknown }) {
  return (
    <pre className="max-h-80 overflow-auto whitespace-pre-wrap rounded bg-ink/[0.03] p-3 text-xs text-ink">
      {JSON.stringify(value, null, 2)}
    </pre>
  );
}

function ResultBlock({ result }: { result: ModuleInvokeResponse }) {
  return (
    <div>
      <p className="mb-2 text-xs text-ink-muted">
        {result.toolName} at {formatTime(result.invokedAt)}
      </p>
      <JsonBlock value={result.json ?? result.content} />
    </div>
  );
}

function summarizeHealthResult(json?: Record<string, unknown>): Array<{ label: string; value: string }> {
  if (!json) return [];
  const rows: Array<{ label: string; value: string }> = [];
  const provider = readNested(json, ['selectedProvider']) || readNested(json, ['providerName']) || readNested(json, ['provider']);
  const lifecycle = readNested(json, ['lifecycle']) || readNested(json, ['status', 'lifecycle']);
  const configured = readNested(json, ['configured']) || readNested(json, ['status', 'configured']);
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
  const credentials =
    readNested(json, ['credentials']) ||
    readNested(json, ['googleHealth', 'credentials']) ||
    readNested(json, ['status', 'credentials']);

  if (provider) rows.push({ label: 'Provider status', value: provider });
  if (lifecycle) rows.push({ label: 'Lifecycle', value: lifecycle });
  if (configured) rows.push({ label: 'Configured', value: configured });
  if (connected) rows.push({ label: 'Connected', value: connected });
  if (credentials) rows.push({ label: 'Credentials', value: credentials });
  if (readiness) rows.push({ label: 'Latest brief', value: readiness });
  if (snapshots) rows.push({ label: 'Storage summary', value: snapshots });
  if (warnings) rows.push({ label: 'Warnings', value: warnings });
  if (caveats) rows.push({ label: 'Data quality', value: caveats });
  return rows;
}

function readNested(value: Record<string, unknown>, path: string[]): string {
  let current: unknown = value;
  for (const key of path) {
    if (!current || typeof current !== 'object' || !(key in current)) return '';
    current = (current as Record<string, unknown>)[key];
  }
  if (current === null || current === undefined) return '';
  return typeof current === 'string' ? current : JSON.stringify(current);
}

function formatCommand(execution: { command: string; args: string[] }) {
  return [execution.command, ...execution.args].join(' ');
}

function formatTime(iso: string): string {
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

function isInvokeResponse(value: unknown): value is ModuleInvokeResponse {
  return Boolean(value && typeof value === 'object' && 'toolName' in value && 'content' in value);
}

function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

function todayMinus(days: number): string {
  const date = new Date();
  date.setDate(date.getDate() - days);
  return date.toISOString().slice(0, 10);
}
