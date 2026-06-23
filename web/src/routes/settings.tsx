import { createFileRoute } from '@tanstack/react-router';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  AlertCircle,
  Braces,
  Check,
  ChevronDown,
  ChevronRight,
  CircleDot,
  Cog,
  Copy,
  FileText,
  FolderOpen,
  Globe,
  Headphones,
  List as ListIcon,
  Loader2,
  MapPin,
  Mic,
  Plug,
  Plus,
  RefreshCw,
  Sliders,
  Square,
  Terminal,
  X,
} from 'lucide-react';
import { PageScaffold } from '../components/PageScaffold';
import {
  getSettings,
  putSettings,
  testLlm,
  getAudioDevices,
  getPiperVoices,
  checkVoiceHostHealth,
  getRuntimeInfo,
  stopRuntime,
  getGatekeeperStatus,
  type TestLlmResponse,
  type AudioDevicesResponse,
  type PiperVoiceEntry,
  type VoiceHostHealthResponse,
  type RuntimeInfo,
  type GatekeeperStatusResponse,
} from '../lib/settingsApi';
import {
  getDiagnostics,
  getRuntimeLog,
  getTurnTrace,
  listRuntimeLogs,
  listTurnTraces,
} from '../lib/activityApi';
import type {
  DiagnosticsResponse,
  RuntimeLogResponse,
  RuntimeLogSummary,
  TurnTraceResponse,
  TurnTraceSummary,
} from '@thaddeus/shared-types';
import { readRuntimeMetadata } from '../lib/runtime';
import { acquireMicStream, clearMicResolutionCache, stopMicStream } from '../lib/micCapture';
import { warmVoiceHost } from '../lib/voiceApi';
import {
  applyTheme,
  readThemePreference,
  watchSystemTheme,
  writeThemePreference,
  type ThemePreference,
} from '../lib/theme';
import type {
  SettingsDocument,
  LocationSettings,
  LimitsSettings,
  UiPreferencesSettings,
  FilesSettings,
} from '@thaddeus/shared-types';
import { ModuleSettingsPanel } from '../components/modules/ModuleSettingsPanel';

export const Route = createFileRoute('/settings')({
  component: SettingsRoute,
});

type TabId = 'general' | 'models' | 'audio' | 'files' | 'location' | 'modules' | 'logs' | 'advanced';
type LogPaneId = 'traces' | 'runtime';
type TraceViewMode = 'events' | 'raw';

const TABS: ReadonlyArray<{ id: TabId; label: string; icon: typeof Cog }> = [
  { id: 'general', label: 'General', icon: Cog },
  { id: 'models', label: 'Models', icon: Sliders },
  { id: 'audio', label: 'Audio & Voice', icon: Headphones },
  { id: 'files', label: 'Files', icon: FolderOpen },
  { id: 'location', label: 'Location', icon: MapPin },
  { id: 'modules', label: 'Modules', icon: Plug },
  { id: 'logs', label: 'Logs', icon: FileText },
  { id: 'advanced', label: 'Advanced', icon: Globe },
];

interface ProviderPreset {
  id: string;
  label: string;
  description: string;
  baseUrl: string;
  needsKey: boolean;
  modelPlaceholder: string;
}

const PROVIDER_PRESETS: ProviderPreset[] = [
  {
    id: 'lmstudio',
    label: 'LM Studio',
    description: 'Local OpenAI-compatible server (default port 1234).',
    baseUrl: 'http://127.0.0.1:1234/v1',
    needsKey: false,
    modelPlaceholder: 'auto (uses currently loaded model)',
  },
  {
    id: 'ollama',
    label: 'Ollama',
    description: 'Local Ollama with the OpenAI-compatible shim.',
    baseUrl: 'http://127.0.0.1:11434/v1',
    needsKey: false,
    modelPlaceholder: 'e.g. llama3.1:8b',
  },
  {
    id: 'openai',
    label: 'OpenAI',
    description: 'Hosted OpenAI API. Disables local-only mode.',
    baseUrl: 'https://api.openai.com/v1',
    needsKey: true,
    modelPlaceholder: 'e.g. gpt-4o-mini',
  },
  {
    id: 'custom',
    label: 'Custom (OpenAI-compatible)',
    description: 'Any OpenAI-compatible endpoint.',
    baseUrl: '',
    needsKey: false,
    modelPlaceholder: 'model id',
  },
];

function findPreset(provider: string, baseUrl: string | null | undefined): ProviderPreset {
  const byId = PROVIDER_PRESETS.find((p) => p.id === provider.toLowerCase());
  if (byId) return byId;
  const byUrl = PROVIDER_PRESETS.find(
    (p) => p.baseUrl && baseUrl && baseUrl.replace(/\/$/, '') === p.baseUrl.replace(/\/$/, ''),
  );
  return byUrl ?? PROVIDER_PRESETS[PROVIDER_PRESETS.length - 1];
}

const DEFAULT_LOCATION: LocationSettings = {
  manualLocation: null,
  use24HourTime: false,
  preferredUnits: 'imperial',
};

const DEFAULT_LIMITS: LimitsSettings = {
  maxToolCallsPerTurn: 12,
  maxToolCallsPerSession: 200,
  maxWebPullsPerTurn: 12,
  maxFileOpsPerMinute: 30,
};

const DEFAULT_UI_PREFS: UiPreferencesSettings = {
  sendOnEnter: true,
  autoSwitchToPermissions: true,
  autoConnectOnStartup: true,
  autoStartLocalRuntime: true,
  minimizeToTrayOnClose: true,
};

const DEFAULT_FILES: FilesSettings = {
  allowedRoots: [],
  disableAllFileAccess: false,
  maxDefaultCharsPerRead: 4000,
};

function SettingsRoute() {
  const [doc, setDoc] = useState<SettingsDocument | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<string | null>(null);
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<TestLlmResponse | null>(null);
  const [gatekeeperStatus, setGatekeeperStatus] = useState<GatekeeperStatusResponse | null>(null);
  const [activeTab, setActiveTab] = useState<TabId>(() =>
    typeof window !== 'undefined' && window.location.hash === '#modules' ? 'modules' : 'general',
  );

  useEffect(() => {
    let cancelled = false;
    getSettings()
      .then((d) => {
        if (!cancelled) {
          setDoc(withDefaults(d));
          setLoading(false);
        }
      })
      .catch((e: Error) => {
        if (!cancelled) {
          setError(e.message);
          setLoading(false);
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // Probe the LLM endpoint on mount once settings have loaded so the Model
  // (and Gatekeeper model) fields render as dropdowns without requiring the
  // user to click "Test connection" first. Stays a silent probe — no error
  // toast if LM Studio isn't running; we just fall back to the text input.
  useEffect(() => {
    if (!doc || testResult || testing) return;
    if (!doc.llm.baseUrl) return;
    let cancelled = false;
    setTesting(true);
    testLlm({ baseUrl: doc.llm.baseUrl, apiKey: doc.llm.apiKey ?? undefined })
      .then((result) => {
        if (!cancelled) setTestResult(result);
      })
      .catch(() => {
        /* silent probe — user can click Test connection for details */
      })
      .finally(() => {
        if (!cancelled) setTesting(false);
      });
    return () => {
      cancelled = true;
    };
    // Only re-run when the baseUrl actually changes (e.g. provider switch).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [doc?.llm.baseUrl]);

  // Refresh the gatekeeper status whenever any gatekeeper-relevant setting
  // changes. The server endpoint reads from the persisted settings, so this
  // lags a freshly-edited-but-unsaved value — intentional, the indicator
  // reflects what the runtime is actually using right now.
  useEffect(() => {
    let cancelled = false;
    getGatekeeperStatus()
      .then((s) => {
        if (!cancelled) setGatekeeperStatus(s);
      })
      .catch(() => {
        /* best-effort — leave existing status in place on transient errors */
      });
    return () => {
      cancelled = true;
    };
  }, [
    doc?.llm.gatekeeperModelId,
    doc?.llm.gatekeeperBaseUrl,
    doc?.llm.baseUrl,
    doc?.llm.modelId,
    doc?.llm.reusePrimaryForGatekeeperOnSharedEndpoint,
    savedAt,
  ]);

  const preset = useMemo(
    () => (doc ? findPreset(doc.llm.provider, doc.llm.baseUrl) : PROVIDER_PRESETS[0]),
    [doc],
  );

  const onProviderChange = (id: string) => {
    if (!doc) return;
    const p = PROVIDER_PRESETS.find((x) => x.id === id) ?? PROVIDER_PRESETS[0];
    setDoc({
      ...doc,
      llm: {
        ...doc.llm,
        provider: p.id,
        baseUrl: p.baseUrl || doc.llm.baseUrl,
        modelId: p.id === 'lmstudio' ? 'auto' : doc.llm.modelId,
      },
      privacy: {
        ...doc.privacy,
        localOnly: p.id === 'openai' ? false : doc.privacy.localOnly,
      },
    });
    setTestResult(null);
  };

  const onTest = async () => {
    if (!doc || testing) return;
    setTesting(true);
    setTestResult(null);
    try {
      const result = await testLlm({
        baseUrl: doc.llm.baseUrl ?? undefined,
        apiKey: doc.llm.apiKey ?? undefined,
      });
      setTestResult(result);
      if (result.ok && result.models.length > 0) {
        if (doc.llm.modelId === 'auto' || !result.models.includes(doc.llm.modelId)) {
          if (doc.llm.provider !== 'lmstudio') {
            setDoc({ ...doc, llm: { ...doc.llm, modelId: result.models[0] } });
          }
        }
      }
    } catch (e) {
      setTestResult({ ok: false, message: (e as Error).message, models: [] });
    } finally {
      setTesting(false);
    }
  };

  const changeTab = (tab: TabId) => {
    setActiveTab(tab);
    if (typeof window === 'undefined') return;
    const hash = tab === 'modules' ? '#modules' : '';
    window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}${hash}`);
  };

  const onSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!doc || saving) return;
    setSaving(true);
    setError(null);
    try {
      const saved = await putSettings(doc);
      setDoc(withDefaults(saved));
      setSavedAt(new Date().toLocaleTimeString());
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setSaving(false);
    }
  };

  return (
    <PageScaffold
      testId="route-settings"
      title="Settings"
      subtitle="Connect a model, configure voice, and tune privacy."
      width="wide"
    >
      {loading ? (
        <p className="text-sm text-ink-muted" data-testid="settings-loading">
          Loading…
        </p>
      ) : !doc ? (
        <p className="text-sm text-rose-500" data-testid="settings-error">
          {error ?? 'Could not load settings.'}
        </p>
      ) : (
        <form onSubmit={onSubmit} data-testid="settings-form">
          <TabBar active={activeTab} onChange={changeTab} />

          <div className="mt-10 pb-32">
            {activeTab === 'general' ? (
              <GeneralTab doc={doc} setDoc={setDoc} />
            ) : null}
            {activeTab === 'models' ? (
              <ModelsTab
                doc={doc}
                setDoc={setDoc}
                preset={preset}
                onProviderChange={onProviderChange}
                onTest={onTest}
                testing={testing}
                testResult={testResult}
                gatekeeperStatus={gatekeeperStatus}
              />
            ) : null}
            {activeTab === 'audio' ? <AudioTab doc={doc} setDoc={setDoc} /> : null}
            {activeTab === 'files' ? <FilesTab doc={doc} setDoc={setDoc} /> : null}
            {activeTab === 'location' ? <LocationTab doc={doc} setDoc={setDoc} /> : null}
            {activeTab === 'modules' ? <ModuleSettingsPanel /> : null}
            {activeTab === 'logs' ? <LogsTab /> : null}
            {activeTab === 'advanced' ? <AdvancedTab doc={doc} setDoc={setDoc} /> : null}

            {error ? (
              <p data-testid="settings-error" className="mt-6 text-sm text-rose-500">
                {error}
              </p>
            ) : null}
          </div>

          {/* Floating save bar — uses the full-width canvas bg with a top fade. */}
          {activeTab !== 'modules' ? (
            <div className="fixed inset-x-0 bottom-0 z-30 border-t border-line bg-canvas/85 backdrop-blur">
            <div className="mx-auto flex w-full max-w-5xl items-center justify-between gap-3 px-6 py-3 md:px-10">
              <span className="text-[11px] text-ink-subtle">
                Saved to <code className="font-mono text-[11px]">~/.thaddeus/runtime-settings.json</code>
              </span>
              <div className="flex items-center gap-4">
                {savedAt ? (
                  <span
                    data-testid="settings-saved"
                    className="inline-flex items-center gap-1 text-xs text-ink-muted"
                  >
                    <Check className="h-3.5 w-3.5" strokeWidth={2} />
                    Saved at {savedAt}
                  </span>
                ) : null}
                <button
                  type="submit"
                  data-testid="settings-save"
                  disabled={saving}
                  className="inline-flex items-center gap-1.5 rounded-full bg-accent px-5 py-2 text-sm font-medium text-white transition hover:opacity-90 disabled:opacity-50"
                >
                  {saving ? (
                    <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.75} />
                  ) : null}
                  {saving ? 'Saving…' : 'Save changes'}
                </button>
              </div>
            </div>
            </div>
          ) : null}
        </form>
      )}
    </PageScaffold>
  );
}

function withDefaults(doc: SettingsDocument): SettingsDocument {
  return {
    ...doc,
    privacy: {
      ...doc.privacy,
      offlineMode: doc.privacy.offlineMode ?? false,
    },
    location: doc.location ?? DEFAULT_LOCATION,
    limits: doc.limits ?? DEFAULT_LIMITS,
    uiPrefs: doc.uiPrefs ?? DEFAULT_UI_PREFS,
  };
}

function TabBar({ active, onChange }: { active: TabId; onChange: (id: TabId) => void }) {
  return (
    <div
      role="tablist"
      aria-label="Settings categories"
      className="-mx-1 flex flex-wrap border-b border-line"
      data-testid="settings-tabs"
    >
      {TABS.map(({ id, label, icon: Icon }) => {
        const selected = id === active;
        return (
          <button
            key={id}
            type="button"
            role="tab"
            aria-selected={selected}
            data-testid={`settings-tab-${id}`}
            onClick={() => onChange(id)}
            className={`relative inline-flex items-center gap-2 px-4 py-3 text-sm font-medium transition-colors ${
              selected
                ? 'text-ink'
                : 'text-ink-muted hover:text-ink'
            }`}
          >
            <Icon className="h-4 w-4" strokeWidth={1.75} />
            <span>{label}</span>
            {selected ? (
              <span
                aria-hidden
                className="absolute inset-x-3 -bottom-px h-[2px] rounded-full bg-accent"
              />
            ) : null}
          </button>
        );
      })}
    </div>
  );
}

// ───────────────────────── General ─────────────────────────

function GeneralTab({
  doc,
  setDoc,
}: {
  doc: SettingsDocument;
  setDoc: (d: SettingsDocument) => void;
}) {
  const ui = doc.uiPrefs ?? DEFAULT_UI_PREFS;
  return (
    <div className="space-y-6" role="tabpanel" aria-labelledby="settings-tab-general">
      <SystemStatusSection />

      <ThemeSection />

      <Section
        title="Desktop behavior"
        description="Defaults for sending, auto-connect, and system tray."
      >
        <Toggle
          testId="settings-ui-send-on-enter"
          label="Send on Enter"
          description="Use Shift+Enter to insert a newline instead."
          checked={ui.sendOnEnter}
          onChange={(v) => setDoc({ ...doc, uiPrefs: { ...ui, sendOnEnter: v } })}
        />
        <Toggle
          testId="settings-ui-auto-switch-permissions"
          label="Auto-switch to Permissions on approval request"
          description="Jumps to the permissions view when the runtime asks for approval."
          checked={ui.autoSwitchToPermissions}
          onChange={(v) => setDoc({ ...doc, uiPrefs: { ...ui, autoSwitchToPermissions: v } })}
        />
        <Toggle
          testId="settings-ui-auto-connect"
          label="Auto-connect on startup"
          description="Reconnect to the last-known runtime as soon as the app opens."
          checked={ui.autoConnectOnStartup}
          onChange={(v) => setDoc({ ...doc, uiPrefs: { ...ui, autoConnectOnStartup: v } })}
        />
        <Toggle
          testId="settings-ui-auto-start-runtime"
          label="Auto-start local runtime if unavailable"
          description="Launches the bundled runtime when no connection is found."
          checked={ui.autoStartLocalRuntime}
          onChange={(v) => setDoc({ ...doc, uiPrefs: { ...ui, autoStartLocalRuntime: v } })}
        />
        <Toggle
          testId="settings-ui-minimize-to-tray"
          label="Minimize to tray on close"
          description="Keeps the app running in the system tray when you close the window."
          checked={ui.minimizeToTrayOnClose}
          onChange={(v) => setDoc({ ...doc, uiPrefs: { ...ui, minimizeToTrayOnClose: v } })}
        />
      </Section>

      <Section title="Shortcuts" description="Global hotkeys for push-to-talk and stop.">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Push-to-talk">
            <input
              data-testid="settings-shortcut-ptt"
              type="text"
              value={doc.shortcuts.pushToTalk}
              onChange={(e) =>
                setDoc({ ...doc, shortcuts: { ...doc.shortcuts, pushToTalk: e.target.value } })
              }
              className={inputCls}
            />
          </Field>
          <Field label="Stop everything">
            <input
              data-testid="settings-shortcut-stop"
              type="text"
              value={doc.shortcuts.stopAll}
              onChange={(e) =>
                setDoc({ ...doc, shortcuts: { ...doc.shortcuts, stopAll: e.target.value } })
              }
              className={inputCls}
            />
          </Field>
        </div>
      </Section>

      <Section title="Privacy" description="Defaults are private. Opt in only to what you need.">
        <Toggle
          testId="settings-privacy-offline-mode"
          label="Offline mode"
          description="Hide and block web, browser, weather, places, feeds, and other network-backed tools."
          checked={doc.privacy.offlineMode ?? false}
          onChange={(v) => setDoc({ ...doc, privacy: { ...doc.privacy, offlineMode: v } })}
        />
        <Toggle
          testId="settings-privacy-local-only"
          label="Local-only mode"
          description="Refuse network calls. Hosted providers will be blocked."
          checked={doc.privacy.localOnly}
          onChange={(v) => setDoc({ ...doc, privacy: { ...doc.privacy, localOnly: v } })}
        />
        <Toggle
          testId="settings-privacy-screen-capture"
          label="Allow screen capture for context"
          description="Sir Thaddeus may capture the active window when you ask it to."
          checked={doc.privacy.allowScreenCapture}
          onChange={(v) =>
            setDoc({ ...doc, privacy: { ...doc.privacy, allowScreenCapture: v } })
          }
        />
        <Toggle
          testId="settings-privacy-telemetry"
          label="Send anonymous usage telemetry"
          description="Off by default. Helps improve the product, never includes your data."
          checked={doc.privacy.telemetryEnabled}
          onChange={(v) =>
            setDoc({ ...doc, privacy: { ...doc.privacy, telemetryEnabled: v } })
          }
        />
      </Section>
    </div>
  );
}

// ───────────────────────── Models ─────────────────────────

function ModelsTab({
  doc,
  setDoc,
  preset,
  onProviderChange,
  onTest,
  testing,
  testResult,
  gatekeeperStatus,
}: {
  doc: SettingsDocument;
  setDoc: (d: SettingsDocument) => void;
  preset: ProviderPreset;
  onProviderChange: (id: string) => void;
  onTest: () => void;
  testing: boolean;
  testResult: TestLlmResponse | null;
  gatekeeperStatus: GatekeeperStatusResponse | null;
}) {
  return (
    <div className="space-y-6" role="tabpanel" aria-labelledby="settings-tab-models">
      <Section
        title="Primary model"
        description="Sir Thaddeus talks to any OpenAI-compatible endpoint."
      >
        <Field label="Provider">
          <Select
            testId="settings-llm-provider"
            value={preset.id}
            onChange={onProviderChange}
            options={PROVIDER_PRESETS.map((p) => ({ value: p.id, label: p.label }))}
          />
          <p className="mt-1.5 text-xs text-ink-muted">{preset.description}</p>
        </Field>

        <Field label="Base URL">
          <input
            data-testid="settings-llm-base-url"
            type="text"
            value={doc.llm.baseUrl ?? ''}
            placeholder={preset.baseUrl}
            onChange={(e) =>
              setDoc({ ...doc, llm: { ...doc.llm, baseUrl: e.target.value || null } })
            }
            className={inputCls}
          />
        </Field>

        {preset.needsKey ? (
          <Field label="API key">
            <input
              data-testid="settings-llm-api-key"
              type="password"
              placeholder="sk-…   (leave as *** to keep current)"
              value={doc.llm.apiKey ?? ''}
              onChange={(e) =>
                setDoc({ ...doc, llm: { ...doc.llm, apiKey: e.target.value || null } })
              }
              className={inputCls}
            />
          </Field>
        ) : (
          <input
            data-testid="settings-llm-api-key"
            type="hidden"
            value={doc.llm.apiKey ?? ''}
            onChange={() => undefined}
          />
        )}

        <div className="flex flex-wrap items-center gap-3">
          <button
            type="button"
            onClick={onTest}
            disabled={testing}
            data-testid="settings-llm-test"
            className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3.5 py-1.5 text-sm font-medium text-ink shadow-soft transition hover:bg-accent-soft disabled:opacity-50"
          >
            {testing ? (
              <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.75} />
            ) : (
              <Plug className="h-4 w-4" strokeWidth={1.75} />
            )}
            {testing ? 'Testing…' : 'Test connection'}
          </button>
          {testResult ? <ConnectionStatus result={testResult} /> : null}
        </div>

        <Field label="Model">
          <ModelCombobox
            testId="settings-llm-model"
            value={doc.llm.modelId}
            onChange={(v) => setDoc({ ...doc, llm: { ...doc.llm, modelId: v } })}
            placeholder={preset.modelPlaceholder}
            options={buildModelOptions(
              testResult?.models ?? [],
              doc.llm.modelId,
              preset.id === 'lmstudio' ? { value: 'auto', label: 'auto (currently loaded)' } : null,
            )}
          />
        </Field>
      </Section>

      <Section
        title="Verification model (gatekeeper)"
        description="Small fast model used to pre-classify each turn so the primary model only sees the tools that make sense for what you asked. Falls back to the primary endpoint when the base URL is blank."
      >
        {gatekeeperStatus ? <GatekeeperStatusBanner status={gatekeeperStatus} /> : null}
        <Toggle
          testId="settings-gatekeeper-enabled"
          label="Enable gatekeeper"
          description="Turn off to send every tool to the primary model on every turn. Model ID + base URL are preserved when disabled."
          checked={doc.llm.gatekeeperEnabled ?? true}
          onChange={(v) =>
            setDoc({
              ...doc,
              llm: { ...doc.llm, gatekeeperEnabled: v },
            })
          }
        />
        <Field label="Gatekeeper base URL">
          <input
            data-testid="settings-gatekeeper-base-url"
            type="text"
            value={doc.llm.gatekeeperBaseUrl ?? ''}
            placeholder={`Falls back to ${doc.llm.baseUrl ?? 'primary'}`}
            onChange={(e) =>
              setDoc({
                ...doc,
                llm: { ...doc.llm, gatekeeperBaseUrl: e.target.value || null },
              })
            }
            className={inputCls}
          />
        </Field>
        <Field label="Gatekeeper model ID">
          <ModelCombobox
            testId="settings-gatekeeper-model-id"
            value={doc.llm.gatekeeperModelId ?? ''}
            onChange={(v) =>
              setDoc({ ...doc, llm: { ...doc.llm, gatekeeperModelId: v || null } })
            }
            placeholder="liquid/lfm2.5-1.2b"
            options={buildModelOptions(testResult?.models ?? [], doc.llm.gatekeeperModelId ?? '')}
          />
        </Field>
        <Toggle
          testId="settings-gatekeeper-reuse-primary"
          label="Reuse primary model when endpoints match"
          description="Falls back to the primary model instead of using a separate gatekeeper model."
          checked={doc.llm.reusePrimaryForGatekeeperOnSharedEndpoint ?? false}
          onChange={(v) =>
            setDoc({
              ...doc,
              llm: { ...doc.llm, reusePrimaryForGatekeeperOnSharedEndpoint: v },
            })
          }
        />
      </Section>

      <Section title="Response shaping" description="Controls the shape of model output.">
        <div className="grid gap-4 sm:grid-cols-3">
          <Field label="Max tokens">
            <input
              data-testid="settings-llm-max-tokens"
              type="number"
              min={1}
              step={1}
              value={doc.llm.maxTokens}
              onChange={(e) =>
                setDoc({
                  ...doc,
                  llm: {
                    ...doc.llm,
                    maxTokens: parsePositiveInt(e.target.value, doc.llm.maxTokens),
                  },
                })
              }
              className={inputCls}
            />
          </Field>
          <Field label="Context window">
            <input
              data-testid="settings-llm-context-window"
              type="number"
              min={1}
              step={1}
              value={doc.llm.contextWindowTokens}
              onChange={(e) =>
                setDoc({
                  ...doc,
                  llm: {
                    ...doc.llm,
                    contextWindowTokens: parsePositiveInt(
                      e.target.value,
                      doc.llm.contextWindowTokens,
                    ),
                  },
                })
              }
              className={inputCls}
            />
          </Field>
          <Field label="Temperature">
            <input
              data-testid="settings-llm-temperature"
              type="number"
              min={0}
              max={2}
              step={0.05}
              value={doc.llm.temperature}
              onChange={(e) =>
                setDoc({
                  ...doc,
                  llm: {
                    ...doc.llm,
                    temperature: clampFloat(e.target.value, doc.llm.temperature, 0, 2),
                  },
                })
              }
              className={inputCls}
            />
          </Field>
        </div>
      </Section>
    </div>
  );
}

// ───────────────────────── Audio & Voice ─────────────────────────

const KOKORO_VOICE_IDS: ReadonlyArray<string> = [
  'af_alloy',
  'af_aoede',
  'af_bella',
  'af_heart',
  'af_jessica',
  'af_kore',
  'af_nicole',
  'af_nova',
  'af_river',
  'af_sarah',
  'af_sky',
  'am_adam',
  'am_echo',
  'am_eric',
  'am_fenrir',
  'am_liam',
  'am_michael',
  'am_onyx',
  'am_puck',
  'am_santa',
  'bf_alice',
  'bf_emma',
  'bf_isabella',
  'bf_lily',
  'bm_daniel',
  'bm_fable',
  'bm_george',
  'bm_lewis',
  'ef_dora',
  'em_alex',
  'em_santa',
  'ff_siwis',
  'hf_alpha',
  'hf_beta',
  'hm_omega',
  'hm_psi',
  'if_sara',
  'im_nicola',
  'jf_alpha',
  'jf_gongitsune',
  'jf_nezumi',
  'jf_tebukuro',
  'jm_kumo',
  'pf_dora',
  'pm_alex',
  'pm_santa',
  'zf_xiaobei',
  'zf_xiaoni',
  'zf_xiaoxiao',
  'zf_xiaoyi',
];

function AudioTab({
  doc,
  setDoc,
}: {
  doc: SettingsDocument;
  setDoc: (d: SettingsDocument) => void;
}) {
  const [devices, setDevices] = useState<AudioDevicesResponse | null>(null);
  const [devicesLoading, setDevicesLoading] = useState(false);
  const [devicesError, setDevicesError] = useState<string | null>(null);
  const [voices, setVoices] = useState<PiperVoiceEntry[] | null>(null);
  const [voicesLoading, setVoicesLoading] = useState(false);
  const [voicesError, setVoicesError] = useState<string | null>(null);
  const [hostHealth, setHostHealth] = useState<VoiceHostHealthResponse | null>(null);
  const [hostHealthChecking, setHostHealthChecking] = useState(false);
  const ttsProvider = normalizeTtsProvider(doc.voice.ttsProvider);
  const isPiperLegacy = ttsProvider === 'piper';

  const refreshDevices = async () => {
    if (devicesLoading) return;
    setDevicesLoading(true);
    setDevicesError(null);
    try {
      setDevices(await getAudioDevices());
    } catch (e) {
      setDevicesError((e as Error).message);
    } finally {
      setDevicesLoading(false);
    }
  };

  const refreshVoices = async () => {
    if (voicesLoading) return;
    if (!isPiperLegacy) {
      setVoices(null);
      setVoicesError(null);
      return;
    }

    setVoicesLoading(true);
    setVoicesError(null);
    try {
      const res = await getPiperVoices();
      setVoices(res.voices);
    } catch (e) {
      setVoicesError((e as Error).message);
    } finally {
      setVoicesLoading(false);
    }
  };

  const probeHost = async () => {
    if (hostHealthChecking) return;
    setHostHealthChecking(true);
    try {
      setHostHealth(await checkVoiceHostHealth(true));
    } catch (e) {
      setHostHealth({ ok: false, message: (e as Error).message, body: null, elapsedMs: 0 });
    } finally {
      setHostHealthChecking(false);
    }
  };

  useEffect(() => {
    void refreshDevices();
    if (isPiperLegacy) void refreshVoices();
    void warmVoiceHost().catch(() => undefined);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const inputOptions = useMemo(
    () => buildDeviceOptions(devices?.inputs, doc.audio.inputDeviceName),
    [devices, doc.audio.inputDeviceName],
  );
  const outputOptions = useMemo(
    () => buildDeviceOptions(devices?.outputs, doc.audio.outputDeviceName),
    [devices, doc.audio.outputDeviceName],
  );
  const voiceOptions = useMemo(
    () => buildVoiceOptions(ttsProvider, voices, doc.voice.ttsVoiceId),
    [ttsProvider, voices, doc.voice.ttsVoiceId],
  );

  return (
    <div className="space-y-6" role="tabpanel" aria-labelledby="settings-tab-audio">
      <Section
        title="Audio devices"
        description="Enumerated via winmm.dll on Windows. Selection is matched by product name at runtime."
      >
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Input device">
            <Select
              testId="settings-audio-input-device"
              value={doc.audio.inputDeviceName ?? ''}
              onChange={(v) =>
                setDoc({ ...doc, audio: { ...doc.audio, inputDeviceName: v || null } })
              }
              options={inputOptions}
            />
          </Field>
          <Field label="Output device">
            <Select
              testId="settings-audio-output-device"
              value={doc.audio.outputDeviceName ?? ''}
              onChange={(v) =>
                setDoc({ ...doc, audio: { ...doc.audio, outputDeviceName: v || null } })
              }
              options={outputOptions}
            />
          </Field>
        </div>
        <Field label="Microphone gain">
          <input
            data-testid="settings-audio-input-gain"
            type="number"
            min={0}
            max={2}
            step={0.05}
            value={doc.audio.inputGain}
            onChange={(e) =>
              setDoc({
                ...doc,
                audio: {
                  ...doc.audio,
                  inputGain: clampFloat(e.target.value, doc.audio.inputGain, 0, 2),
                },
              })
            }
            className={inputCls}
          />
        </Field>
        <div className="flex flex-wrap items-center gap-3 text-xs text-ink-muted">
          <button
            type="button"
            onClick={refreshDevices}
            disabled={devicesLoading}
            data-testid="settings-audio-refresh"
            className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3.5 py-1.5 text-sm font-medium text-ink shadow-soft transition hover:bg-accent-soft disabled:opacity-50"
          >
            {devicesLoading ? (
              <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.75} />
            ) : (
              <RefreshCw className="h-4 w-4" strokeWidth={1.75} />
            )}
            Refresh devices
          </button>
          {devicesError ? (
            <span className="text-rose-600" data-testid="settings-audio-devices-error">
              {devicesError}
            </span>
          ) : devices ? (
            <span>
              {devices.inputs.length} inputs · {devices.outputs.length} outputs
            </span>
          ) : null}
        </div>
      </Section>

      <MicTester selectedInputName={doc.audio.inputDeviceName ?? null} />

      <Section
        title="Voice pipeline"
        description="Local VoiceHost orchestrates ASR and TTS through a single process."
      >
        <Toggle
          testId="settings-voice-host-enabled"
          label="Enable Local VoiceHost"
          description="The baseline product profile keeps local voice interaction disabled."
          checked={doc.voice.voiceHostEnabled ?? false}
          onChange={(v) => setDoc({ ...doc, voice: { ...doc.voice, voiceHostEnabled: v } })}
        />
        <Field label="VoiceHost base URL">
          <input
            data-testid="settings-voice-host-base-url"
            type="text"
            value={doc.voice.voiceHostBaseUrl ?? ''}
            placeholder="http://127.0.0.1:17845"
            onChange={(e) =>
              setDoc({
                ...doc,
                voice: { ...doc.voice, voiceHostBaseUrl: e.target.value || null },
              })
            }
            className={inputCls}
          />
        </Field>
        <div className="flex flex-wrap items-center gap-3 text-xs">
          <button
            type="button"
            onClick={probeHost}
            disabled={hostHealthChecking}
            data-testid="settings-voice-host-probe"
            className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3.5 py-1.5 text-sm font-medium text-ink shadow-soft transition hover:bg-accent-soft disabled:opacity-50"
          >
            {hostHealthChecking ? (
              <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.75} />
            ) : (
              <Plug className="h-4 w-4" strokeWidth={1.75} />
            )}
            Check VoiceHost
          </button>
          {hostHealth ? (
            <span
              data-testid="settings-voice-host-result"
              data-ok={hostHealth.ok}
              className={
                hostHealth.ok
                  ? 'inline-flex items-center gap-1.5 rounded-full bg-emerald-50 px-2.5 py-1 text-xs font-medium text-emerald-700'
                  : 'inline-flex items-center gap-1.5 rounded-full bg-rose-50 px-2.5 py-1 text-xs font-medium text-rose-700'
              }
            >
              {hostHealth.ok ? (
                <CircleDot className="h-3.5 w-3.5" strokeWidth={2} />
              ) : (
                <AlertCircle className="h-3.5 w-3.5" strokeWidth={2} />
              )}
              {hostHealth.message}
              {hostHealth.elapsedMs > 0 ? ` (${hostHealth.elapsedMs} ms)` : ''}
            </span>
          ) : null}
        </div>
      </Section>

      <Section title="Text-to-speech" description="Playback engine for spoken responses.">
        <Toggle
          testId="settings-audio-tts-enabled"
          label="Speak responses aloud"
          description="Keeps your selected TTS engine configured, but mutes playback when off."
          checked={doc.audio.ttsEnabled}
          onChange={(v) => setDoc({ ...doc, audio: { ...doc.audio, ttsEnabled: v } })}
        />
        <Field label="TTS engine">
          <Select
            testId="settings-voice-tts"
            value={ttsProvider}
            onChange={(v) =>
              setDoc({
                ...doc,
                voice: {
                  ...doc.voice,
                  ttsProvider: v,
                  ttsVoiceId: defaultVoiceForTtsProvider(v, doc.voice.ttsVoiceId),
                },
              })
            }
            options={[
              { value: 'kokoro-sharp', label: 'KokoroSharp' },
              { value: 'piper', label: 'Piper (legacy fallback)' },
              { value: 'stub', label: 'Disabled' },
            ]}
          />
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Voice">
            <Select
              testId="settings-voice-tts-voice-id"
              value={doc.voice.ttsVoiceId ?? ''}
              onChange={(v) =>
                setDoc({ ...doc, voice: { ...doc.voice, ttsVoiceId: v || null } })
              }
              options={voiceOptions}
            />
          </Field>
          <Field label="TTS model ID (optional)">
            <input
              data-testid="settings-voice-tts-model-id"
              type="text"
              value={doc.voice.ttsModelId ?? ''}
              placeholder="(engine default)"
              onChange={(e) =>
                setDoc({ ...doc, voice: { ...doc.voice, ttsModelId: e.target.value || null } })
              }
              className={inputCls}
            />
          </Field>
        </div>
        {isPiperLegacy ? (
          <div className="flex flex-wrap items-center gap-3 text-xs text-ink-muted">
            <button
              type="button"
              onClick={refreshVoices}
              disabled={voicesLoading}
              data-testid="settings-voice-refresh"
              className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3.5 py-1.5 text-sm font-medium text-ink shadow-soft transition hover:bg-accent-soft disabled:opacity-50"
            >
              {voicesLoading ? (
                <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.75} />
              ) : (
                <RefreshCw className="h-4 w-4" strokeWidth={1.75} />
              )}
              Refresh legacy voices
            </button>
            {voicesError ? (
              <span className="text-rose-600" data-testid="settings-voice-voices-error">
                {voicesError}
              </span>
            ) : voices ? (
              <span>
                {voices.filter((v) => v.isInstalled).length} installed · {voices.length} total
              </span>
            ) : null}
          </div>
        ) : null}
        {isPiperLegacy ? (
          <Field label="Legacy Piper voice path (optional)">
            <input
              data-testid="settings-voice-piper-path"
              type="text"
              value={doc.voice.piperVoicePath ?? ''}
              placeholder="C:\\path\\to\\voice.onnx"
              onChange={(e) =>
                setDoc({
                  ...doc,
                  voice: { ...doc.voice, piperVoicePath: e.target.value || null },
                })
              }
              className={inputCls}
            />
          </Field>
        ) : null}
      </Section>

      <Section title="Speech-to-text" description="Transcription engine for voice input.">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="STT engine">
            <Select
              testId="settings-voice-stt"
              value={doc.voice.sttProvider}
              onChange={(v) => setDoc({ ...doc, voice: { ...doc.voice, sttProvider: v } })}
              options={[
                { value: 'whisper-cpp', label: 'whisper.cpp (local)' },
                { value: 'faster-whisper', label: 'faster-whisper' },
                { value: 'stub', label: 'Disabled' },
              ]}
            />
          </Field>
          <Field label="STT model">
            <Select
              testId="settings-voice-stt-model"
              value={doc.voice.sttModelId ?? 'base'}
              onChange={(v) => setDoc({ ...doc, voice: { ...doc.voice, sttModelId: v } })}
              options={[
                { value: 'base', label: 'base (bundled)' },
                { value: 'tiny.en', label: 'tiny.en (fast English)' },
                { value: 'tiny', label: 'tiny (fast multilingual)' },
                { value: 'base.en', label: 'base.en (English)' },
                { value: 'small.en', label: 'small.en (slower, better)' },
              ]}
            />
          </Field>
          <Field label="STT language">
            <input
              data-testid="settings-voice-stt-language"
              type="text"
              value={doc.voice.sttLanguage ?? ''}
              placeholder="en"
              onChange={(e) =>
                setDoc({ ...doc, voice: { ...doc.voice, sttLanguage: e.target.value || null } })
              }
              className={inputCls}
            />
          </Field>
        </div>
      </Section>

      <Section
        title="YouTube ASR"
        description="Transcription and drafting preferences for YouTube sources."
      >
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="ASR provider">
            <Select
              testId="settings-voice-youtube-provider"
              value={doc.voice.youtubeAsrProvider ?? 'faster-whisper'}
              onChange={(v) =>
                setDoc({ ...doc, voice: { ...doc.voice, youtubeAsrProvider: v } })
              }
              options={[{ value: 'faster-whisper', label: 'faster-whisper' }]}
            />
          </Field>
          <Field label="ASR model ID / size">
            <input
              data-testid="settings-voice-youtube-model"
              type="text"
              value={doc.voice.youtubeAsrModelId ?? ''}
              placeholder="base"
              onChange={(e) =>
                setDoc({
                  ...doc,
                  voice: { ...doc.voice, youtubeAsrModelId: e.target.value || null },
                })
              }
              className={inputCls}
            />
          </Field>
          <Field label="Language hint">
            <input
              data-testid="settings-voice-youtube-language"
              type="text"
              value={doc.voice.youtubeLanguageHint ?? ''}
              placeholder="en-us"
              onChange={(e) =>
                setDoc({
                  ...doc,
                  voice: { ...doc.voice, youtubeLanguageHint: e.target.value || null },
                })
              }
              className={inputCls}
            />
          </Field>
          <Field label="Drafting tone">
            <Select
              testId="settings-voice-youtube-tone"
              value={doc.voice.youtubeDraftTone ?? 'professional'}
              onChange={(v) => setDoc({ ...doc, voice: { ...doc.voice, youtubeDraftTone: v } })}
              options={[
                { value: 'professional', label: 'Professional' },
                { value: 'direct', label: 'Direct' },
                { value: 'playful', label: 'Playful' },
              ]}
            />
          </Field>
        </div>
        <Toggle
          testId="settings-voice-youtube-keep-audio"
          label="Keep audio files"
          description="Preserves downloaded audio for re-use instead of deleting after transcription."
          checked={doc.voice.youtubeKeepAudio ?? false}
          onChange={(v) => setDoc({ ...doc, voice: { ...doc.voice, youtubeKeepAudio: v } })}
        />
      </Section>
    </div>
  );
}

// ───────────────────────── Files ─────────────────────────

function FilesTab({
  doc,
  setDoc,
}: {
  doc: SettingsDocument;
  setDoc: (d: SettingsDocument) => void;
}) {
  const files = doc.files ?? DEFAULT_FILES;
  const [draft, setDraft] = useState('');
  const [err, setErr] = useState<string | null>(null);

  const addRoot = () => {
    const trimmed = draft.trim();
    if (!trimmed) {
      setErr('Enter a folder path.');
      return;
    }
    // Basic sanity: must look like an absolute path.
    const looksAbsolute = /^([a-zA-Z]:[\\/])|^[\\/]|^\\\\/.test(trimmed);
    if (!looksAbsolute) {
      setErr('Use an absolute path (e.g. C:\\Users\\you\\Documents or /home/you/docs).');
      return;
    }
    if (files.allowedRoots.some((r) => r.toLowerCase() === trimmed.toLowerCase())) {
      setErr('That folder is already on the list.');
      return;
    }
    setErr(null);
    setDraft('');
    setDoc({
      ...doc,
      files: { ...files, allowedRoots: [...files.allowedRoots, trimmed] },
    });
  };

  const removeRoot = (path: string) => {
    setDoc({
      ...doc,
      files: {
        ...files,
        allowedRoots: files.allowedRoots.filter((r) => r !== path),
      },
    });
  };

  return (
    <div className="space-y-6" role="tabpanel" aria-labelledby="settings-tab-files">
      <Section
        title="Allowed folders (read-only)"
        description="The assistant can READ files inside the folders listed here. It cannot write, modify, move, or delete anything in these folders — only read what you ask it about. Add or remove folders to change what's authorized."
      >
        <div
          className="mb-3 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-800 dark:text-amber-200"
          data-testid="settings-files-write-notice"
        >
          Writing or editing files is a separate, opt-in feature that's still in development.
          Nothing on this page grants write access.
        </div>

        <div className="space-y-2">
          {files.allowedRoots.length === 0 ? (
            <div className="rounded-xl border border-dashed border-line px-4 py-5 text-center text-[13px] text-ink-muted">
              No folders allowed yet. Add one below to let file tools work.
            </div>
          ) : (
            <ul className="divide-y divide-line rounded-xl border border-line" data-testid="settings-files-roots">
              {files.allowedRoots.map((path) => (
                <li
                  key={path}
                  data-testid={`settings-files-root-${encodeURIComponent(path)}`}
                  className="flex items-center gap-3 px-3 py-2.5"
                >
                  <FolderOpen className="h-4 w-4 shrink-0 text-ink-subtle" strokeWidth={1.75} />
                  <span className="flex-1 truncate font-mono text-[13px] text-ink">{path}</span>
                  <span className="text-[10px] font-semibold uppercase tracking-[0.08em] text-ink-subtle">
                    Read-only
                  </span>
                  <button
                    type="button"
                    onClick={() => removeRoot(path)}
                    aria-label={`Remove ${path}`}
                    data-testid={`settings-files-remove-${encodeURIComponent(path)}`}
                    className="rounded-full p-1 text-ink-muted transition-colors hover:bg-rose-500/10 hover:text-rose-500"
                  >
                    <X className="h-4 w-4" strokeWidth={1.75} />
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>

        <Field label="Add folder (absolute path)">
          <div className="flex items-center gap-2">
            <input
              data-testid="settings-files-add-input"
              type="text"
              value={draft}
              placeholder={isWindowsPlatform() ? 'C:\\Users\\you\\Documents' : '/home/you/docs'}
              onChange={(e) => {
                setDraft(e.target.value);
                if (err) setErr(null);
              }}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault();
                  addRoot();
                }
              }}
              className={inputCls}
            />
            <button
              type="button"
              onClick={addRoot}
              data-testid="settings-files-add-button"
              className="inline-flex items-center gap-1.5 rounded-full bg-accent px-3.5 py-2.5 text-sm font-medium text-white transition hover:opacity-90"
            >
              <Plus className="h-4 w-4" strokeWidth={2} />
              Add
            </button>
          </div>
          {err ? (
            <p data-testid="settings-files-add-error" className="mt-1.5 text-[12px] text-rose-500">
              {err}
            </p>
          ) : (
            <p className="mt-1.5 text-[12px] text-ink-subtle">
              Tip: the tool only reads files; writes require a separate permission.
            </p>
          )}
        </Field>
      </Section>

      <Section
        title="Reading limits"
        description="Controls for how much content tools return from a single read."
      >
        <Field label="Max characters per read">
          <input
            data-testid="settings-files-max-chars"
            type="number"
            min={100}
            step={100}
            value={files.maxDefaultCharsPerRead}
            onChange={(e) =>
              setDoc({
                ...doc,
                files: {
                  ...files,
                  maxDefaultCharsPerRead: parsePositiveInt(
                    e.target.value,
                    files.maxDefaultCharsPerRead,
                  ),
                },
              })
            }
            className={inputCls}
          />
        </Field>

        <Toggle
          testId="settings-files-disable-all"
          label="Disable all file access"
          description="Hard kill-switch: every file tool refuses regardless of folders above."
          checked={files.disableAllFileAccess}
          onChange={(v) =>
            setDoc({ ...doc, files: { ...files, disableAllFileAccess: v } })
          }
        />
      </Section>
    </div>
  );
}

function isWindowsPlatform(): boolean {
  if (typeof navigator === 'undefined') return false;
  return /Win|Windows/i.test(navigator.platform) || /Windows/i.test(navigator.userAgent);
}

// ───────────────────────── Location ─────────────────────────

function LocationTab({
  doc,
  setDoc,
}: {
  doc: SettingsDocument;
  setDoc: (d: SettingsDocument) => void;
}) {
  const loc = doc.location ?? DEFAULT_LOCATION;
  return (
    <div className="space-y-6" role="tabpanel" aria-labelledby="settings-tab-location">
      <Section
        title="Location"
        description="Used for nearby-places searches and local weather. Manual only — no geolocation."
      >
        <Field label="Manual location (city/state, ZIP, or country)">
          <input
            data-testid="settings-location-manual"
            type="text"
            value={loc.manualLocation ?? ''}
            placeholder="e.g., Olympia, WA"
            onChange={(e) =>
              setDoc({
                ...doc,
                location: { ...loc, manualLocation: e.target.value || null },
              })
            }
            className={inputCls}
          />
        </Field>
      </Section>

      <Section title="Display" description="Time format and measurement preferences.">
        <Toggle
          testId="settings-location-24h"
          label="Use 24-hour time (military time)"
          description="Renders timestamps as 17:30 instead of 5:30 PM."
          checked={loc.use24HourTime}
          onChange={(v) =>
            setDoc({ ...doc, location: { ...loc, use24HourTime: v } })
          }
        />
        <Field label="Preferred units">
          <Select
            testId="settings-location-units"
            value={loc.preferredUnits || 'imperial'}
            onChange={(v) =>
              setDoc({ ...doc, location: { ...loc, preferredUnits: v } })
            }
            options={[
              { value: 'imperial', label: 'Imperial (°F, miles)' },
              { value: 'metric', label: 'Metric (°C, km)' },
              { value: 'auto', label: 'Auto (infer from source)' },
            ]}
          />
        </Field>
      </Section>
    </div>
  );
}

// ───────────────────────── Logs ─────────────────────────

function LogsTab() {
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
        ) : (
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
        )}
      </Section>
    </div>
  );
}

function TurnTracePane({
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

function RuntimeLogsPane({
  logs,
  error,
  selectedFileName,
  selectedSummary,
  log,
  loading,
  logError,
  onSelect,
}: {
  logs: RuntimeLogSummary[] | null;
  error: string | null;
  selectedFileName: string | null;
  selectedSummary: RuntimeLogSummary | null;
  log: RuntimeLogResponse | null;
  loading: boolean;
  logError: string | null;
  onSelect: (fileName: string) => void;
}) {
  if (error) {
    return (
      <p className="text-sm text-rose-500" data-testid="settings-runtime-logs-error">
        {error}
      </p>
    );
  }

  if (logs === null) {
    return (
      <p className="text-sm italic text-ink-subtle" data-testid="settings-runtime-logs-loading">
        Loading...
      </p>
    );
  }

  if (logs.length === 0) {
    return (
      <p className="text-sm italic text-ink-subtle" data-testid="settings-runtime-logs-empty">
        No runtime log files found.
      </p>
    );
  }

  return (
    <div className="grid gap-4 lg:grid-cols-[minmax(250px,0.42fr)_minmax(0,1fr)]" data-testid="settings-runtime-log-browser">
      <div className="min-w-0 overflow-hidden rounded-lg border border-line bg-canvas-raised/40">
        <div className="border-b border-line px-3 py-2 text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
          Log files
        </div>
        <div className="max-h-[32rem] overflow-auto" data-testid="settings-runtime-logs-list">
          {logs.map((logSummary) => {
            const isSelected = selectedFileName === logSummary.fileName;
            return (
              <button
                key={logSummary.fileName}
                type="button"
                onClick={() => onSelect(logSummary.fileName)}
                aria-current={isSelected ? 'true' : undefined}
                data-testid={`settings-runtime-log-row-${logSummary.fileName}`}
                className={`block w-full border-b border-line/70 px-3 py-3 text-left transition last:border-b-0 ${
                  isSelected ? 'bg-accent-soft/80 text-ink' : 'hover:bg-canvas-sunken/60'
                }`}
              >
                <div className="truncate font-mono text-[12px] text-ink">{logSummary.fileName}</div>
                <div className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-[11px] uppercase tracking-[0.08em] text-ink-subtle">
                  <span>{formatRelativeTime(logSummary.modifiedAt)}</span>
                  <span>{logSummary.lineCount} lines</span>
                  <span>{formatBytes(logSummary.sizeBytes)}</span>
                </div>
                {logSummary.lastLine ? (
                  <div className="mt-1 line-clamp-2 text-[11px] leading-4 text-ink-muted">
                    {logSummary.lastLine}
                  </div>
                ) : null}
              </button>
            );
          })}
        </div>
      </div>

      <RuntimeLogDetailPanel
        selectedFileName={selectedFileName}
        selectedSummary={selectedSummary}
        log={log}
        loading={loading}
        error={logError}
      />
    </div>
  );
}

function RuntimeLogDetailPanel({
  selectedFileName,
  selectedSummary,
  log,
  loading,
  error,
}: {
  selectedFileName: string | null;
  selectedSummary: RuntimeLogSummary | null;
  log: RuntimeLogResponse | null;
  loading: boolean;
  error: string | null;
}) {
  return (
    <div className="min-w-0 rounded-lg border border-line bg-canvas-raised/40" data-testid="settings-runtime-log-view">
      <div className="border-b border-line px-4 py-3">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
              Selected log
            </div>
            <div className="mt-1 truncate font-mono text-[13px] text-ink">
              {selectedFileName ?? 'No log selected'}
            </div>
          </div>
          {selectedSummary ? (
            <div className="flex flex-wrap gap-2 text-[11px] text-ink-muted">
              <span>{selectedSummary.lineCount} lines</span>
              <span>{formatBytes(selectedSummary.sizeBytes)}</span>
              <span>{formatAbsoluteTime(selectedSummary.modifiedAt)}</span>
            </div>
          ) : null}
        </div>
      </div>

      <div className="max-h-[32rem] overflow-auto p-4">
        {!selectedFileName ? (
          <p className="text-sm italic text-ink-subtle">No log selected.</p>
        ) : loading ? (
          <p className="text-xs italic text-ink-subtle">Loading log...</p>
        ) : error ? (
          <p className="text-sm text-rose-500" data-testid="settings-runtime-log-error">{error}</p>
        ) : log && log.lines.length > 0 ? (
          <ol className="space-y-1" data-testid="settings-runtime-log-lines">
            {log.lines.map((line) => (
              <li key={`${log.fileName}-${line.number}`} className="grid grid-cols-[4.5rem_minmax(0,1fr)] gap-3 rounded-md px-2 py-1.5 text-xs hover:bg-canvas-sunken/60">
                <span className="select-none text-right font-mono text-ink-subtle">{line.number}</span>
                <span className="min-w-0 whitespace-pre-wrap break-words font-mono leading-5 text-ink-muted">{line.text}</span>
              </li>
            ))}
          </ol>
        ) : (
          <p className="text-xs italic text-ink-subtle">Log file is empty.</p>
        )}
      </div>
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

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '0 B';
  if (bytes < 1024) return `${bytes} B`;
  const kibibytes = bytes / 1024;
  if (kibibytes < 1024) return `${kibibytes.toFixed(kibibytes >= 10 ? 0 : 1)} KB`;
  const mebibytes = kibibytes / 1024;
  return `${mebibytes.toFixed(mebibytes >= 10 ? 0 : 1)} MB`;
}

function formatAbsoluteTime(iso: string): string {
  const value = new Date(iso);
  if (Number.isNaN(value.getTime())) return iso;
  return value.toLocaleString();
}

function formatRelativeTime(iso: string): string {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return iso;
  const seconds = Math.floor((Date.now() - then) / 1000);
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return new Date(iso).toLocaleString();
}

// ───────────────────────── Advanced ─────────────────────────

function AdvancedTab({
  doc,
  setDoc,
}: {
  doc: SettingsDocument;
  setDoc: (d: SettingsDocument) => void;
}) {
  const limits = doc.limits ?? DEFAULT_LIMITS;
  return (
    <div className="space-y-6" role="tabpanel" aria-labelledby="settings-tab-advanced">
      <Section
        title="Limits and tool budgets"
        description="Guardrails for tool-heavy sessions. Saved but not yet enforced by the runtime."
      >
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Max tool calls per turn">
            <input
              data-testid="settings-limits-tools-per-turn"
              type="number"
              min={1}
              step={1}
              value={limits.maxToolCallsPerTurn}
              onChange={(e) =>
                setDoc({
                  ...doc,
                  limits: {
                    ...limits,
                    maxToolCallsPerTurn: parsePositiveInt(
                      e.target.value,
                      limits.maxToolCallsPerTurn,
                    ),
                  },
                })
              }
              className={inputCls}
            />
          </Field>
          <Field label="Max tool calls per session">
            <input
              data-testid="settings-limits-tools-per-session"
              type="number"
              min={1}
              step={1}
              value={limits.maxToolCallsPerSession}
              onChange={(e) =>
                setDoc({
                  ...doc,
                  limits: {
                    ...limits,
                    maxToolCallsPerSession: parsePositiveInt(
                      e.target.value,
                      limits.maxToolCallsPerSession,
                    ),
                  },
                })
              }
              className={inputCls}
            />
          </Field>
          <Field label="Max web pulls per turn">
            <input
              data-testid="settings-limits-web-per-turn"
              type="number"
              min={1}
              step={1}
              value={limits.maxWebPullsPerTurn}
              onChange={(e) =>
                setDoc({
                  ...doc,
                  limits: {
                    ...limits,
                    maxWebPullsPerTurn: parsePositiveInt(
                      e.target.value,
                      limits.maxWebPullsPerTurn,
                    ),
                  },
                })
              }
              className={inputCls}
            />
          </Field>
          <Field label="Max file ops per minute">
            <input
              data-testid="settings-limits-files-per-minute"
              type="number"
              min={1}
              step={1}
              value={limits.maxFileOpsPerMinute}
              onChange={(e) =>
                setDoc({
                  ...doc,
                  limits: {
                    ...limits,
                    maxFileOpsPerMinute: parsePositiveInt(
                      e.target.value,
                      limits.maxFileOpsPerMinute,
                    ),
                  },
                })
              }
              className={inputCls}
            />
          </Field>
        </div>
      </Section>
    </div>
  );
}

// ───────────────────────── Theme ─────────────────────────

function ThemeSection() {
  const [pref, setPref] = useState<ThemePreference>(() => readThemePreference());

  useEffect(() => {
    // Keep "system" mode in sync with OS theme switches without requiring
    // a tab reload. The watch returns a disposer; call it on unmount.
    return watchSystemTheme(() => pref);
  }, [pref]);

  const choose = (next: ThemePreference) => {
    setPref(next);
    writeThemePreference(next);
    // Re-apply explicitly in case the change was a system swap-in/out so the
    // class on <html> reflects the new effective theme immediately.
    applyTheme(next);
  };

  const options: ReadonlyArray<{ value: ThemePreference; label: string; hint: string }> = [
    { value: 'light', label: 'Light', hint: 'Always light, ignores OS theme.' },
    { value: 'dark', label: 'Dark', hint: 'Always dark, ignores OS theme.' },
    { value: 'system', label: 'System', hint: 'Follows your OS appearance setting.' },
  ];

  return (
    <Section
      title="Appearance"
      description="Light, dark, or follow the system. Stored locally in this browser."
    >
      <div className="grid gap-2 sm:grid-cols-3" data-testid="settings-theme-picker">
        {options.map((o) => {
          const selected = pref === o.value;
          return (
            <button
              key={o.value}
              type="button"
              role="radio"
              aria-checked={selected}
              data-testid={`settings-theme-${o.value}`}
              onClick={() => choose(o.value)}
              className={`rounded-xl border px-3.5 py-3 text-left transition ${
                selected
                  ? 'border-accent bg-accent-soft text-ink'
                  : 'border-line bg-canvas-raised text-ink-muted hover:text-ink'
              }`}
            >
              <p className="text-sm font-medium text-ink">{o.label}</p>
              <p className="mt-0.5 text-[12px] text-ink-muted">{o.hint}</p>
            </button>
          );
        })}
      </div>
    </Section>
  );
}

// ───────────────────────── System status ─────────────────────────

function SystemStatusSection() {
  const [info, setInfo] = useState<RuntimeInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [stopping, setStopping] = useState(false);
  const [stopNote, setStopNote] = useState<string | null>(null);
  const meta = readRuntimeMetadata();
  const runtimeUrl = typeof window !== 'undefined' ? window.location.origin : '';

  const refresh = async () => {
    setError(null);
    try {
      setInfo(await getRuntimeInfo());
    } catch (e) {
      setError((e as Error).message);
    }
  };

  useEffect(() => {
    void refresh();
    const id = window.setInterval(() => void refresh(), 5000);
    return () => window.clearInterval(id);
  }, []);

  const onStop = async () => {
    if (stopping) return;
    if (!window.confirm('Stop the runtime? The UI will disconnect until you relaunch.')) return;
    setStopping(true);
    setStopNote(null);
    try {
      await stopRuntime();
      setStopNote('Runtime shutdown requested. Re-launch the shell to restart.');
    } catch (e) {
      setStopNote((e as Error).message);
    } finally {
      setStopping(false);
    }
  };

  const runtimeLabel = info
    ? `${runtimeUrl}/  (v${info.version})`
    : runtimeUrl
      ? `${runtimeUrl}/  (v${meta.version})`
      : '—';
  const managedLabel = info
    ? info.managedByShell
      ? `Managed runtime: running (shell pid ${info.parentPid})`
      : 'Unmanaged (started directly)'
    : error
      ? error
      : 'Loading…';
  const uptimeLabel = info ? formatUptime(info.uptimeMs) : '';

  return (
    <section
      data-testid="settings-system-status"
      className="space-y-8 pb-10 border-b border-line"
    >
      <header>
        <h2 className="text-[15px] font-semibold tracking-tight text-ink">System status</h2>
        <p className="mt-1 text-[13px] text-ink-muted">Live status before you change anything.</p>
      </header>

      <div className="grid gap-6 md:grid-cols-3">
        <StatusCard
          label="Current runtime"
          value={runtimeLabel}
          testId="settings-status-runtime"
          tone="ok"
        />
        <StatusCard
          label="Managed service"
          value={managedLabel}
          testId="settings-status-managed"
          tone={info?.managedByShell ? 'ok' : 'neutral'}
        />
        <StatusCard
          label="Uptime"
          value={uptimeLabel || '—'}
          testId="settings-status-state"
          tone="neutral"
        />
      </div>

      <div className="grid gap-8 md:grid-cols-2">
        <div>
          <h3 className="text-[14px] font-medium text-ink">Connection target</h3>
          <p className="mt-1 text-[13px] text-ink-muted">Endpoint for the runtime connection.</p>
          <input
            data-testid="settings-status-connection-target"
            type="text"
            readOnly
            value={runtimeUrl || 'unknown'}
            className={`mt-3 ${inputCls} text-ink-muted`}
          />
          <p className="mt-2 text-[12px] text-ink-subtle">
            Loopback for local. To point at a different runtime, launch the shell with a new lock file.
          </p>
        </div>

        <div>
          <h3 className="text-[14px] font-medium text-ink">Runtime controls</h3>
          <p className="mt-1 text-[13px] text-ink-muted">Start or stop the managed runtime.</p>
          <div className="mt-3 flex flex-wrap gap-2">
            <button
              type="button"
              disabled
              title="Starting a runtime from the UI requires the shell."
              data-testid="settings-runtime-start"
              className="inline-flex items-center gap-1.5 rounded-full border border-line px-3.5 py-1.5 text-sm font-medium text-ink-subtle disabled:cursor-not-allowed"
            >
              Start local runtime
            </button>
            <button
              type="button"
              onClick={onStop}
              disabled={stopping || (info ? !info.managedByShell : false)}
              data-testid="settings-runtime-stop"
              className="inline-flex items-center gap-1.5 rounded-full border border-line px-3.5 py-1.5 text-sm font-medium text-ink transition-colors hover:bg-accent-soft disabled:cursor-not-allowed disabled:opacity-50"
            >
              {stopping ? (
                <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.75} />
              ) : null}
              Stop managed runtime
            </button>
          </div>
          {stopNote ? (
            <p data-testid="settings-runtime-stop-note" className="mt-2 text-[12px] text-ink-muted">
              {stopNote}
            </p>
          ) : (
            <p className="mt-2 text-[12px] text-ink-subtle">
              {info?.managedByShell
                ? 'Manages the local runtime process.'
                : 'This runtime was not started by the shell; controls are read-only.'}
            </p>
          )}
        </div>
      </div>
    </section>
  );
}

function StatusCard({
  label,
  value,
  testId,
  tone,
}: {
  label: string;
  value: string;
  testId: string;
  tone?: 'ok' | 'warn' | 'neutral';
}) {
  const dotClass =
    tone === 'ok'
      ? 'bg-accent'
      : tone === 'warn'
        ? 'bg-amber-500'
        : 'bg-ink-subtle';
  return (
    <div data-testid={testId}>
      <p className="text-[11px] font-medium text-ink-muted">{label}</p>
      <p className="mt-1.5 flex items-center gap-2 text-[14px] font-medium text-ink">
        <span aria-hidden className={`inline-block h-1.5 w-1.5 rounded-full ${dotClass}`} />
        <span className="truncate">{value || '—'}</span>
      </p>
    </div>
  );
}

function formatUptime(ms: number): string {
  if (ms < 1000) return '< 1 s';
  const totalSec = Math.floor(ms / 1000);
  const h = Math.floor(totalSec / 3600);
  const m = Math.floor((totalSec % 3600) / 60);
  const s = totalSec % 60;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

// ───────────────────────── Shared components ─────────────────────────

const inputCls =
  'block w-full rounded-xl border border-line bg-canvas-raised px-3.5 py-2.5 text-sm text-ink placeholder:text-ink-subtle transition-colors focus:border-accent-ring focus:outline-none focus:ring-2 focus:ring-accent/20';

function MicTester({ selectedInputName }: { selectedInputName: string | null }) {
  const [active, setActive] = useState(false);
  const [level, setLevel] = useState(0);
  const [peak, setPeak] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [info, setInfo] = useState<{ requested: string | null; resolved: string | null; usedDefault: boolean } | null>(null);
  const [diag, setDiag] = useState<{ trackLabel: string; readyState: string; muted: boolean; deviceId: string; sampleRate: number; channelCount: number; ctxState: string } | null>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const audioCtxRef = useRef<AudioContext | null>(null);
  const analyserRef = useRef<AnalyserNode | null>(null);
  const rafRef = useRef<number | null>(null);

  const stop = useCallbackRef(() => {
    if (rafRef.current !== null) {
      cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
    }
    if (audioCtxRef.current) {
      try { void audioCtxRef.current.close(); } catch { /* ignore */ }
      audioCtxRef.current = null;
    }
    analyserRef.current = null;
    if (streamRef.current) {
      stopMicStream(streamRef.current);
      streamRef.current = null;
    }
    setActive(false);
    setLevel(0);
    setDiag(null);
  });

  useEffect(() => () => { stop(); }, [stop]);

  // If the user changes the selected input device while the tester is
  // running, re-resolve so the next start picks up the new selection.
  useEffect(() => {
    clearMicResolutionCache();
  }, [selectedInputName]);

  const start = async () => {
    setError(null);
    setPeak(0);
    try {
      const acquired = await acquireMicStream();
      streamRef.current = acquired.stream;
      setInfo({
        requested: acquired.requestedName,
        resolved: acquired.resolvedLabel,
        usedDefault: acquired.usedDefault,
      });

      const AudioCtx = window.AudioContext || (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
      if (!AudioCtx) {
        throw new Error('Web Audio API is not available in this browser.');
      }
      const ctx = new AudioCtx();
      audioCtxRef.current = ctx;
      // WebView2 / Edge frequently starts AudioContext in 'suspended'
      // state until a user gesture explicitly resumes it. Without this
      // the analyser silently sees no samples and the meter stays at 0%.
      if (ctx.state === 'suspended') {
        try { await ctx.resume(); } catch { /* surfaced via diag below */ }
      }
      const source = ctx.createMediaStreamSource(acquired.stream);
      const analyser = ctx.createAnalyser();
      analyser.fftSize = 1024;
      analyser.smoothingTimeConstant = 0.6;
      source.connect(analyser);
      analyserRef.current = analyser;

      const track = acquired.stream.getAudioTracks()[0];
      const settings = track?.getSettings?.() ?? {};
      setDiag({
        trackLabel: track?.label ?? '(no track)',
        readyState: track?.readyState ?? 'unknown',
        muted: track?.muted ?? false,
        deviceId: typeof settings.deviceId === 'string' ? settings.deviceId : '(unknown)',
        sampleRate: typeof settings.sampleRate === 'number' ? settings.sampleRate : 0,
        channelCount: typeof settings.channelCount === 'number' ? settings.channelCount : 0,
        ctxState: ctx.state,
      });

      const buffer = new Uint8Array(analyser.fftSize);
      let runningPeak = 0;
      const tick = () => {
        const a = analyserRef.current;
        if (!a) return;
        a.getByteTimeDomainData(buffer);
        let sumSq = 0;
        for (let i = 0; i < buffer.length; i++) {
          const v = (buffer[i] - 128) / 128;
          sumSq += v * v;
        }
        const rms = Math.sqrt(sumSq / buffer.length);
        const normalized = Math.min(1, rms * 1.8);
        setLevel(normalized);
        if (normalized > runningPeak) {
          runningPeak = normalized;
          setPeak(runningPeak);
        }
        rafRef.current = requestAnimationFrame(tick);
      };
      setActive(true);
      rafRef.current = requestAnimationFrame(tick);
    } catch (e) {
      stop();
      setError((e as Error).message || 'Could not start the microphone test.');
    }
  };

  const levelPct = Math.round(level * 100);
  const peakPct = Math.round(peak * 100);

  return (
    <Section
      title="Test microphone"
      description="Captures audio in the browser shell so you can confirm the selected device is actually picking up your voice before using push-to-talk."
    >
      <div className="flex flex-wrap items-center gap-3">
        {active ? (
          <button
            type="button"
            onClick={stop}
            data-testid="settings-mic-tester-stop"
            className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3.5 py-1.5 text-sm font-medium text-ink shadow-soft transition hover:bg-accent-soft"
          >
            <Square className="h-4 w-4" strokeWidth={1.75} />
            Stop test
          </button>
        ) : (
          <button
            type="button"
            onClick={() => { void start(); }}
            data-testid="settings-mic-tester-start"
            className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3.5 py-1.5 text-sm font-medium text-ink shadow-soft transition hover:bg-accent-soft"
          >
            <Mic className="h-4 w-4" strokeWidth={1.75} />
            Start test
          </button>
        )}
        {active ? (
          <span className="text-xs text-ink-muted">Speak into your microphone &mdash; the bar should jump.</span>
        ) : null}
      </div>

      <div className="space-y-2">
        <div
          className="relative h-3 w-full overflow-hidden rounded-full bg-canvas-raised border border-line"
          data-testid="settings-mic-tester-level"
        >
          <div
            className="absolute inset-y-0 left-0 bg-emerald-500 transition-[width] duration-75"
            style={{ width: `${levelPct}%` }}
          />
          {peak > 0 ? (
            <div
              className="absolute top-0 bottom-0 w-px bg-emerald-700"
              style={{ left: `${peakPct}%` }}
              aria-hidden
            />
          ) : null}
        </div>
        <div className="flex justify-between text-[11px] text-ink-muted">
          <span>Level: {levelPct}%</span>
          <span>Peak: {peakPct}%</span>
        </div>
      </div>

      {info ? (
        <div className="rounded-md border border-line bg-canvas-raised p-3 text-xs space-y-1">
          <div>
            <span className="text-ink-muted">Selected in settings: </span>
            <span className="text-ink">{info.requested ?? 'System default'}</span>
          </div>
          <div>
            <span className="text-ink-muted">Browser opened: </span>
            <span className="text-ink">{info.resolved ?? 'unknown'}</span>
          </div>
          {info.usedDefault && info.requested ? (
            <div className="text-amber-600" data-testid="settings-mic-tester-fallback">
              Selected device was not found in the browser; fell back to the system default.
              Try clicking &ldquo;Refresh devices&rdquo; above and re-selecting.
            </div>
          ) : null}
          {diag ? (
            <div className="mt-2 border-t border-line pt-2 text-[11px] text-ink-muted space-y-0.5" data-testid="settings-mic-tester-diag">
              <div>Track label: <span className="text-ink">{diag.trackLabel}</span></div>
              <div>Track readyState: <span className="text-ink">{diag.readyState}</span>{diag.muted ? <span className="text-amber-600"> (muted by OS)</span> : null}</div>
              <div>Audio context: <span className="text-ink">{diag.ctxState}</span></div>
              <div>Sample rate: <span className="text-ink">{diag.sampleRate || 'unknown'}</span> &middot; channels: <span className="text-ink">{diag.channelCount || 'unknown'}</span></div>
              <div>deviceId: <span className="text-ink break-all">{diag.deviceId}</span></div>
            </div>
          ) : null}
        </div>
      ) : null}

      {error ? (
        <div className="text-xs text-rose-600" data-testid="settings-mic-tester-error">
          {error}
        </div>
      ) : null}
    </Section>
  );
}

// Stable callback ref so cleanup effects don't re-fire on every render.
function useCallbackRef<T extends (...args: never[]) => unknown>(fn: T): T {
  const ref = useRef(fn);
  ref.current = fn;
  return useRef(((...args: never[]) => ref.current(...args)) as T).current;
}

function Section({
  title,
  description,
  children,
}: {
  title: string;
  description?: string;
  children: ReactNode;
}) {
  return (
    <section className="space-y-5 pb-10 border-b border-line last:border-0 last:pb-0">
      <header>
        <h2 className="text-[15px] font-semibold tracking-tight text-ink">{title}</h2>
        {description ? (
          <p className="mt-1 text-[13px] text-ink-muted">{description}</p>
        ) : null}
      </header>
      <div className="space-y-5">{children}</div>
    </section>
  );
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1.5 block text-[12px] font-medium text-ink-muted">
        {label}
      </span>
      {children}
    </label>
  );
}

function Select({
  testId,
  value,
  onChange,
  options,
}: {
  testId: string;
  value: string;
  onChange: (v: string) => void;
  options: ReadonlyArray<{ value: string; label: string }>;
}) {
  return (
    <div className="relative">
      <select
        data-testid={testId}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className={`${inputCls} appearance-none pr-9`}
      >
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
      <ChevronDown
        className="pointer-events-none absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 text-ink-subtle"
        strokeWidth={1.75}
      />
    </div>
  );
}

function Toggle({
  testId,
  label,
  description,
  checked,
  onChange,
}: {
  testId: string;
  label: string;
  description?: string;
  checked: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <div className="flex items-start justify-between gap-6 py-1">
      <div className="min-w-0">
        <p className="text-[14px] font-medium text-ink">{label}</p>
        {description ? (
          <p className="mt-0.5 text-[13px] text-ink-muted">{description}</p>
        ) : null}
      </div>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        aria-label={label}
        onClick={() => onChange(!checked)}
        className={`relative h-[22px] w-[38px] shrink-0 rounded-full transition-colors ${
          checked ? 'bg-accent' : 'bg-line-strong'
        }`}
      >
        <span
          className={`absolute top-0.5 left-0.5 h-[18px] w-[18px] rounded-full bg-white shadow-sm transition-transform ${
            checked ? 'translate-x-4' : 'translate-x-0'
          }`}
        />
      </button>
      <input
        type="checkbox"
        data-testid={testId}
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        className="sr-only"
      />
    </div>
  );
}

/**
 * Builds the option list for {@link ModelCombobox}. Unions the server-returned
 * model ids with the currently-saved value (so edits survive even if the
 * server can't be reached right now). When `currentValue` isn't in the
 * available list we mark it `(saved)` so the user can tell what was persisted
 * before the probe. Optional leading fixed entry (e.g. "auto") goes first.
 */
function buildModelOptions(
  available: ReadonlyArray<string>,
  currentValue: string,
  fixedFirst: { value: string; label: string } | null = null,
): ReadonlyArray<{ value: string; label: string }> {
  const opts: { value: string; label: string }[] = [];
  if (fixedFirst) opts.push(fixedFirst);
  const seen = new Set<string>(opts.map((o) => o.value));
  for (const m of available) {
    if (seen.add(m)) opts.push({ value: m, label: m });
  }
  if (currentValue && !seen.has(currentValue)) {
    opts.push({ value: currentValue, label: `${currentValue} (saved)` });
  }
  return opts;
}

/**
 * Text input + custom dropdown list. Always lets the user type a freeform id,
 * and on focus (or chevron click) shows every known option regardless of what
 * is currently in the field. Typed text filters the list, but a zero-match
 * filter falls back to showing all options so the user can always discover
 * what is available.
 *
 * Tests interact with this via `fill()` on the input element, same as a
 * plain text field — the dropdown is a pure UX affordance.
 */
function ModelCombobox({
  testId,
  value,
  onChange,
  placeholder,
  options,
}: {
  testId: string;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  options: ReadonlyArray<{ value: string; label: string }>;
}) {
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  // `query` = text the user is actively typing for filtering. `null` means
  // "not filtering" → show all options. Reset to `null` on open/close so the
  // dropdown always starts by showing the full list.
  const [query, setQuery] = useState<string | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);
  const listId = `${testId}-listbox`;

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) {
        setOpen(false);
        setQuery(null);
      }
    };
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [open]);

  const visibleOptions = useMemo(() => {
    if (query === null || query === '') return options;
    const q = query.toLowerCase();
    const matches = options.filter(
      (o) => o.value.toLowerCase().includes(q) || o.label.toLowerCase().includes(q),
    );
    return matches.length > 0 ? matches : options;
  }, [options, query]);

  const commitSelection = (v: string) => {
    onChange(v);
    setQuery(null);
    setActiveIndex(-1);
    setOpen(false);
  };

  const openDropdown = () => {
    setQuery(null);
    setActiveIndex(-1);
    setOpen(true);
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      if (!open) {
        openDropdown();
        return;
      }
      setActiveIndex((i) => Math.min(visibleOptions.length - 1, i + 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      if (!open) {
        openDropdown();
        return;
      }
      setActiveIndex((i) => Math.max(0, i - 1));
    } else if (e.key === 'Enter') {
      if (open && activeIndex >= 0 && activeIndex < visibleOptions.length) {
        e.preventDefault();
        commitSelection(visibleOptions[activeIndex].value);
      }
    } else if (e.key === 'Escape') {
      if (open) {
        e.preventDefault();
        setOpen(false);
        setQuery(null);
      }
    }
  };

  return (
    <div ref={rootRef} className="relative">
      <input
        data-testid={testId}
        type="text"
        role="combobox"
        aria-expanded={open}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={
          open && activeIndex >= 0 ? `${listId}-opt-${activeIndex}` : undefined
        }
        value={query ?? value}
        placeholder={placeholder}
        onFocus={openDropdown}
        onChange={(e) => {
          const next = e.target.value;
          setQuery(next);
          onChange(next);
          setOpen(true);
          setActiveIndex(-1);
        }}
        onKeyDown={onKeyDown}
        className={`${inputCls} pr-9`}
        autoComplete="off"
        spellCheck={false}
      />
      <button
        type="button"
        tabIndex={-1}
        aria-label="Toggle model list"
        onMouseDown={(e) => {
          e.preventDefault();
          if (open) {
            setOpen(false);
            setQuery(null);
          } else {
            openDropdown();
          }
        }}
        className="absolute right-2 top-1/2 flex h-7 w-7 -translate-y-1/2 items-center justify-center rounded-md text-ink-subtle hover:text-ink"
      >
        <ChevronDown
          className={`h-4 w-4 transition-transform ${open ? 'rotate-180' : ''}`}
          strokeWidth={1.75}
        />
      </button>
      {open && visibleOptions.length > 0 ? (
        <ul
          id={listId}
          role="listbox"
          className="absolute left-0 right-0 top-full z-20 mt-1 max-h-64 overflow-auto rounded-xl border border-line bg-canvas-raised py-1 text-sm shadow-soft"
        >
          {visibleOptions.map((o, idx) => {
            const isActive = idx === activeIndex;
            const isSelected = o.value === value;
            return (
              <li
                key={o.value}
                id={`${listId}-opt-${idx}`}
                role="option"
                aria-selected={isSelected}
                onMouseEnter={() => setActiveIndex(idx)}
                onMouseDown={(e) => {
                  e.preventDefault();
                  commitSelection(o.value);
                }}
                className={`cursor-pointer px-3.5 py-1.5 text-ink ${
                  isActive ? 'bg-accent-soft' : ''
                } ${isSelected ? 'font-medium' : ''}`}
              >
                {o.label}
              </li>
            );
          })}
        </ul>
      ) : null}
    </div>
  );
}

function GatekeeperStatusBanner({ status }: { status: GatekeeperStatusResponse }) {
  let icon: ReactNode;
  let toneCls: string;
  let state: string;

  if (!status.configured) {
    icon = <CircleDot className="h-3.5 w-3.5 opacity-60" strokeWidth={2} />;
    toneCls = 'text-ink-muted';
    state = 'not-configured';
  } else if (status.ok) {
    icon = <CircleDot className="h-3.5 w-3.5" strokeWidth={2} />;
    toneCls = 'text-emerald-600 dark:text-emerald-400';
    state = 'active';
  } else {
    icon = <AlertCircle className="h-3.5 w-3.5" strokeWidth={2} />;
    toneCls = 'text-rose-500';
    state = 'unreachable';
  }

  const reuseNote = status.reusingPrimary
    ? ' · reusing primary client (single-GPU setup)'
    : '';

  return (
    <div
      className={`mb-4 inline-flex items-center gap-1.5 text-xs font-medium ${toneCls}`}
      data-testid="settings-gatekeeper-status"
      data-ok={status.ok}
      data-state={state}
    >
      {icon}
      <span>{status.message}{reuseNote}</span>
    </div>
  );
}

function ConnectionStatus({ result }: { result: TestLlmResponse }) {
  if (result.ok) {
    return (
      <span
        className="inline-flex items-center gap-1.5 text-xs font-medium text-emerald-600 dark:text-emerald-400"
        data-testid="settings-llm-test-result"
        data-ok="true"
      >
        <CircleDot className="h-3.5 w-3.5" strokeWidth={2} />
        {result.message}
      </span>
    );
  }
  return (
    <span
      className="inline-flex items-center gap-1.5 text-xs font-medium text-rose-500"
      data-testid="settings-llm-test-result"
      data-ok="false"
    >
      <AlertCircle className="h-3.5 w-3.5" strokeWidth={2} />
      {result.message}
    </span>
  );
}

function buildDeviceOptions(
  devices: ReadonlyArray<{ displayName: string; productName: string }> | undefined,
  current: string | null | undefined,
): ReadonlyArray<{ value: string; label: string }> {
  const opts: { value: string; label: string }[] = [{ value: '', label: 'System default' }];
  if (devices && devices.length > 0) {
    for (const d of devices) {
      opts.push({ value: d.productName, label: d.displayName });
    }
  }
  if (current && !opts.some((o) => o.value === current)) {
    opts.push({ value: current, label: `${current} (saved)` });
  }
  return opts;
}

function buildVoiceOptions(
  provider: string,
  voices: ReadonlyArray<PiperVoiceEntry> | null | undefined,
  current: string | null | undefined,
): ReadonlyArray<{ value: string; label: string }> {
  if (normalizeTtsProvider(provider) === 'kokoro-sharp') {
    const opts = KOKORO_VOICE_IDS.map((voiceId) => ({
      value: voiceId,
      label: formatKokoroVoiceLabel(voiceId),
    }));
    if (current && current.includes('_') && !opts.some((o) => o.value === current)) {
      opts.push({ value: current, label: `${current} (saved)` });
    }
    return opts;
  }

  const opts: { value: string; label: string }[] = [];
  if (voices && voices.length > 0) {
    for (const v of voices) {
      const suffix = v.isInstalled ? '' : ' (download)';
      opts.push({ value: v.voiceId, label: `${v.displayName} — ${v.voiceId}${suffix}` });
    }
  }
  if (current && !opts.some((o) => o.value === current)) {
    opts.push({ value: current, label: `${current} (saved)` });
  }
  if (opts.length === 0) {
    opts.push({ value: '', label: '(no voices discovered — save to use manual ID)' });
  }
  return opts;
}

function normalizeTtsProvider(provider: string | null | undefined): string {
  const value = (provider ?? '').trim().toLowerCase();
  if (value === 'kokoro' || value === 'kokorosharp' || value === 'kokoro-sharp') {
    return 'kokoro-sharp';
  }
  if (value === 'piper') return 'piper';
  if (value === 'stub' || value === 'disabled' || value === 'none') return 'stub';
  return 'kokoro-sharp';
}

function defaultVoiceForTtsProvider(provider: string, current: string | null | undefined): string | null {
  const normalized = normalizeTtsProvider(provider);
  if (normalized === 'kokoro-sharp') {
    return !current || current.includes('-') || !current.includes('_') ? 'bm_lewis' : current;
  }
  if (normalized === 'piper') {
    return !current || !current.includes('-') ? 'en_US-john-medium' : current;
  }
  return null;
}

function formatKokoroVoiceLabel(voiceId: string): string {
  const name = voiceId.includes('_') ? voiceId.split('_').slice(1).join(' ') : voiceId;
  const displayName = name
    .split(' ')
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ');
  return `${displayName || voiceId} (${voiceId})`;
}

function parsePositiveInt(raw: string, fallback: number): number {
  const next = Number.parseInt(raw, 10);
  return Number.isFinite(next) && next > 0 ? next : fallback;
}

function clampFloat(raw: string, fallback: number, min: number, max: number): number {
  const next = Number.parseFloat(raw);
  if (!Number.isFinite(next)) return fallback;
  return Math.min(max, Math.max(min, next));
}
