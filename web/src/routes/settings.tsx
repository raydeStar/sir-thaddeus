import { createFileRoute } from '@tanstack/react-router';
import { useEffect, useMemo, useState } from 'react';
import {
  AlertCircle,
  Check,
  ChevronDown,
  CircleDot,
  Loader2,
  Plug,
} from 'lucide-react';
import { PageScaffold } from '../components/PageScaffold';
import { getSettings, putSettings, testLlm, type TestLlmResponse } from '../lib/settingsApi';
import type { SettingsDocument } from '@thaddeus/shared-types';

export const Route = createFileRoute('/settings')({
  component: SettingsRoute,
});

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

function SettingsRoute() {
  const [doc, setDoc] = useState<SettingsDocument | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<string | null>(null);
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<TestLlmResponse | null>(null);

  useEffect(() => {
    let cancelled = false;
    getSettings()
      .then((d) => {
        if (!cancelled) {
          setDoc(d);
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
          // For LM Studio, leave 'auto' alone; for others, snap to first model.
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
      setDoc(saved);
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
      bare
    >
      {loading ? (
        <p className="text-sm italic text-ink-muted" data-testid="settings-loading">
          Loading…
        </p>
      ) : !doc ? (
        <p className="text-sm text-rose-600" data-testid="settings-error">
          {error ?? 'Could not load settings.'}
        </p>
      ) : (
        <form onSubmit={onSubmit} data-testid="settings-form" className="space-y-8">
          {/* Language model */}
          <Section
            title="Language model"
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
                      llm: { ...doc.llm, maxTokens: parsePositiveInt(e.target.value, doc.llm.maxTokens) },
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
                        contextWindowTokens: parsePositiveInt(e.target.value, doc.llm.contextWindowTokens),
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
                      llm: { ...doc.llm, temperature: clampFloat(e.target.value, doc.llm.temperature, 0, 2) },
                    })
                  }
                  className={inputCls}
                />
              </Field>
            </div>
            <p className="text-xs text-ink-muted">
              These map directly to the live runtime client now, so save changes applies them without a restart.
            </p>
          </Section>

          {/* Voice */}
          <Section title="Voice" description="Speech-to-text and text-to-speech engines (optional).">
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Speech-to-text">
                <Select
                  testId="settings-voice-stt"
                  value={doc.voice.sttProvider}
                  onChange={(v) => setDoc({ ...doc, voice: { ...doc.voice, sttProvider: v } })}
                  options={[
                    { value: 'whisper-cpp', label: 'whisper.cpp (local)' },
                    { value: 'stub', label: 'Disabled' },
                  ]}
                />
              </Field>
              <Field label="Text-to-speech">
                <Select
                  testId="settings-voice-tts"
                  value={doc.voice.ttsProvider}
                  onChange={(v) => setDoc({ ...doc, voice: { ...doc.voice, ttsProvider: v } })}
                  options={[
                    { value: 'piper', label: 'Piper (local)' },
                    { value: 'stub', label: 'Disabled' },
                  ]}
                />
              </Field>
            </div>
            <Field label="Piper voice path">
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

          <Section
            title="Audio"
            description="Capture gain and spoken-response controls that apply on the next voice turn."
          >
            <div className="grid gap-4 sm:grid-cols-2">
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
            </div>
            <Toggle
              testId="settings-audio-tts-enabled"
              label="Speak responses aloud"
              description="Keeps your selected TTS engine configured, but mutes spoken playback when turned off."
              checked={doc.audio.ttsEnabled}
              onChange={(v) => setDoc({ ...doc, audio: { ...doc.audio, ttsEnabled: v } })}
            />
            <p className="text-xs text-ink-muted">
              Input gain is applied in software before transcription, so save changes takes effect without restarting the runtime.
            </p>
          </Section>

          {/* Shortcuts */}
          <Section title="Shortcuts" description="Global hotkeys for push-to-talk and stop.">
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Push-to-talk">
                <input
                  data-testid="settings-shortcut-ptt"
                  type="text"
                  value={doc.shortcuts.pushToTalk}
                  onChange={(e) =>
                    setDoc({
                      ...doc,
                      shortcuts: { ...doc.shortcuts, pushToTalk: e.target.value },
                    })
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
                    setDoc({
                      ...doc,
                      shortcuts: { ...doc.shortcuts, stopAll: e.target.value },
                    })
                  }
                  className={inputCls}
                />
              </Field>
            </div>
          </Section>

          {/* Privacy */}
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

          {error ? (
            <p data-testid="settings-error" className="text-sm text-rose-600">
              {error}
            </p>
          ) : null}

          {/* Save bar */}
          <div className="sticky bottom-0 -mx-6 mt-2 border-t border-line bg-canvas/90 px-6 py-4 backdrop-blur md:-mx-8 md:px-8">
            <div className="flex items-center justify-between gap-3">
              <span className="text-xs text-ink-subtle">
                Saved to <code className="font-mono">~/.thaddeus/runtime-settings.json</code>
              </span>
              <div className="flex items-center gap-3">
                {savedAt ? (
                  <span
                    data-testid="settings-saved"
                    className="inline-flex items-center gap-1 text-xs text-emerald-700"
                  >
                    <Check className="h-3.5 w-3.5" strokeWidth={2} />
                    Saved at {savedAt}
                  </span>
                ) : null}
                <button
                  type="submit"
                  data-testid="settings-save"
                  disabled={saving}
                  className="inline-flex items-center gap-1.5 rounded-full bg-accent px-4 py-2 text-sm font-medium text-white transition hover:opacity-90 disabled:opacity-50"
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

const inputCls =
  'block w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 text-sm text-ink placeholder:text-ink-subtle shadow-soft focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/15';

function Section({
  title,
  description,
  children,
}: {
  title: string;
  description?: string;
  children: React.ReactNode;
}) {
  return (
    <section className="surface space-y-4 p-5 md:p-6">
      <header>
        <h2 className="text-sm font-semibold tracking-tightest text-ink">{title}</h2>
        {description ? <p className="mt-0.5 text-xs text-ink-muted">{description}</p> : null}
      </header>
      <div className="space-y-4">{children}</div>
    </section>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1.5 block text-xs font-medium uppercase tracking-wide text-ink-muted">
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
    <div className="flex items-start justify-between gap-4 rounded-xl border border-line bg-canvas-sunken/40 px-4 py-3">
      <div className="min-w-0">
        <p className="text-sm font-medium text-ink">{label}</p>
        {description ? <p className="mt-0.5 text-xs text-ink-muted">{description}</p> : null}
      </div>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        aria-label={label}
        onClick={() => onChange(!checked)}
        className={`relative h-6 w-10 shrink-0 rounded-full transition ${
          checked ? 'bg-accent' : 'bg-line-strong'
        }`}
      >
        <span
          className={`absolute top-0.5 left-0.5 h-5 w-5 rounded-full bg-white shadow transition-transform ${
            checked ? 'translate-x-4' : 'translate-x-0'
          }`}
        />
      </button>
      {/* Hidden native checkbox preserves the existing data-testid contract for smoke tests. */}
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
        className="inline-flex items-center gap-1.5 rounded-full bg-emerald-50 px-2.5 py-1 text-xs font-medium text-emerald-700"
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
      className="inline-flex items-center gap-1.5 rounded-full bg-rose-50 px-2.5 py-1 text-xs font-medium text-rose-700"
      data-testid="settings-llm-test-result"
      data-ok="false"
    >
      <AlertCircle className="h-3.5 w-3.5" strokeWidth={2} />
      {result.message}
    </span>
  );
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
