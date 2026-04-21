import { createFileRoute } from '@tanstack/react-router';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  AlertCircle,
  Check,
  ChevronDown,
  CircleDot,
  Cog,
  FolderOpen,
  Globe,
  Headphones,
  Loader2,
  MapPin,
  Plug,
  Plus,
  RefreshCw,
  Sliders,
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
  type TestLlmResponse,
  type AudioDevicesResponse,
  type PiperVoiceEntry,
  type VoiceHostHealthResponse,
  type RuntimeInfo,
} from '../lib/settingsApi';
import { readRuntimeMetadata } from '../lib/runtime';
import type {
  SettingsDocument,
  LocationSettings,
  LimitsSettings,
  UiPreferencesSettings,
  FilesSettings,
} from '@thaddeus/shared-types';

export const Route = createFileRoute('/settings')({
  component: SettingsRoute,
});

type TabId = 'general' | 'models' | 'audio' | 'files' | 'location' | 'advanced';

const TABS: ReadonlyArray<{ id: TabId; label: string; icon: typeof Cog }> = [
  { id: 'general', label: 'General', icon: Cog },
  { id: 'models', label: 'Models', icon: Sliders },
  { id: 'audio', label: 'Audio & Voice', icon: Headphones },
  { id: 'files', label: 'Files', icon: FolderOpen },
  { id: 'location', label: 'Location', icon: MapPin },
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
  const [activeTab, setActiveTab] = useState<TabId>('general');

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
          <TabBar active={activeTab} onChange={setActiveTab} />

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
              />
            ) : null}
            {activeTab === 'audio' ? <AudioTab doc={doc} setDoc={setDoc} /> : null}
            {activeTab === 'files' ? <FilesTab doc={doc} setDoc={setDoc} /> : null}
            {activeTab === 'location' ? <LocationTab doc={doc} setDoc={setDoc} /> : null}
            {activeTab === 'advanced' ? <AdvancedTab doc={doc} setDoc={setDoc} /> : null}

            {error ? (
              <p data-testid="settings-error" className="mt-6 text-sm text-rose-500">
                {error}
              </p>
            ) : null}
          </div>

          {/* Floating save bar — uses the full-width canvas bg with a top fade. */}
          <div className="fixed inset-x-0 bottom-0 z-10 border-t border-line bg-canvas/85 backdrop-blur">
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
        </form>
      )}
    </PageScaffold>
  );
}

function withDefaults(doc: SettingsDocument): SettingsDocument {
  return {
    ...doc,
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
}: {
  doc: SettingsDocument;
  setDoc: (d: SettingsDocument) => void;
  preset: ProviderPreset;
  onProviderChange: (id: string) => void;
  onTest: () => void;
  testing: boolean;
  testResult: TestLlmResponse | null;
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
          {testResult?.ok && testResult.models.length > 0 ? (
            <Select
              testId="settings-llm-model"
              value={doc.llm.modelId}
              onChange={(v) => setDoc({ ...doc, llm: { ...doc.llm, modelId: v } })}
              options={[
                ...(preset.id === 'lmstudio'
                  ? [{ value: 'auto', label: 'auto (currently loaded)' }]
                  : []),
                ...testResult.models.map((m) => ({ value: m, label: m })),
                ...(testResult.models.includes(doc.llm.modelId) || doc.llm.modelId === 'auto'
                  ? []
                  : [{ value: doc.llm.modelId, label: `${doc.llm.modelId} (saved)` }]),
              ]}
            />
          ) : (
            <input
              data-testid="settings-llm-model"
              type="text"
              value={doc.llm.modelId}
              placeholder={preset.modelPlaceholder}
              onChange={(e) => setDoc({ ...doc, llm: { ...doc.llm, modelId: e.target.value } })}
              className={inputCls}
            />
          )}
        </Field>
      </Section>

      <Section
        title="Verification model (gatekeeper)"
        description="Optional smaller model used for verification. Falls back to the primary endpoint when left blank."
      >
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
          <input
            data-testid="settings-gatekeeper-model-id"
            type="text"
            value={doc.llm.gatekeeperModelId ?? ''}
            placeholder="qwen3.5-2b"
            onChange={(e) =>
              setDoc({
                ...doc,
                llm: { ...doc.llm, gatekeeperModelId: e.target.value || null },
              })
            }
            className={inputCls}
          />
        </Field>
        <Toggle
          testId="settings-gatekeeper-reuse-primary"
          label="Reuse primary model when endpoints match"
          description="Avoids LM Studio load/offload churn on single-GPU setups."
          checked={doc.llm.reusePrimaryForGatekeeperOnSharedEndpoint ?? true}
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
      setHostHealth(await checkVoiceHostHealth());
    } catch (e) {
      setHostHealth({ ok: false, message: (e as Error).message, body: null, elapsedMs: 0 });
    } finally {
      setHostHealthChecking(false);
    }
  };

  useEffect(() => {
    void refreshDevices();
    void refreshVoices();
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
    () => buildVoiceOptions(voices, doc.voice.ttsVoiceId),
    [voices, doc.voice.ttsVoiceId],
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
            value={doc.voice.ttsProvider}
            onChange={(v) => setDoc({ ...doc, voice: { ...doc.voice, ttsProvider: v } })}
            options={[
              { value: 'piper', label: 'Piper (local)' },
              { value: 'windows', label: 'Windows SAPI' },
              { value: 'kokoro', label: 'Kokoro' },
              { value: 'stub', label: 'Disabled' },
            ]}
          />
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Piper voice">
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
            Refresh voices
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
        <Field label="Piper voice path (optional)">
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
        title="Allowed folders"
        description="Sir Thaddeus can only read files inside folders you list here. Add the folders you want him to access; remove them to revoke. Paths must be absolute."
      >
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
  voices: ReadonlyArray<PiperVoiceEntry> | null | undefined,
  current: string | null | undefined,
): ReadonlyArray<{ value: string; label: string }> {
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

function parsePositiveInt(raw: string, fallback: number): number {
  const next = Number.parseInt(raw, 10);
  return Number.isFinite(next) && next > 0 ? next : fallback;
}

function clampFloat(raw: string, fallback: number, min: number, max: number): number {
  const next = Number.parseFloat(raw);
  if (!Number.isFinite(next)) return fallback;
  return Math.min(max, Math.max(min, next));
}
