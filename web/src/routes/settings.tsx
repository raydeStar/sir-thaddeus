import { createFileRoute } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { getSettings, putSettings } from '../lib/settingsApi';
import type { SettingsDocument } from '@thaddeus/shared-types';

export const Route = createFileRoute('/settings')({
  component: SettingsRoute,
});

function SettingsRoute() {
  const [doc, setDoc] = useState<SettingsDocument | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<string | null>(null);

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
      subtitle="Model, voice, shortcuts, and privacy."
    >
      {loading ? (
        <p className="text-sm italic text-slate-500" data-testid="settings-loading">
          Loading…
        </p>
      ) : !doc ? (
        <p className="text-sm text-red-600" data-testid="settings-error">
          {error ?? 'Could not load settings.'}
        </p>
      ) : (
        <form onSubmit={onSubmit} data-testid="settings-form" className="space-y-6">
          <Section title="Language model">
            <Field label="Provider">
              <input
                data-testid="settings-llm-provider"
                type="text"
                value={doc.llm.provider}
                onChange={(e) => setDoc({ ...doc, llm: { ...doc.llm, provider: e.target.value } })}
                className={inputCls}
              />
            </Field>
            <Field label="Model id">
              <input
                data-testid="settings-llm-model"
                type="text"
                value={doc.llm.modelId}
                onChange={(e) => setDoc({ ...doc, llm: { ...doc.llm, modelId: e.target.value } })}
                className={inputCls}
              />
            </Field>
            <Field label="Base URL">
              <input
                data-testid="settings-llm-base-url"
                type="text"
                value={doc.llm.baseUrl ?? ''}
                onChange={(e) =>
                  setDoc({ ...doc, llm: { ...doc.llm, baseUrl: e.target.value || null } })
                }
                className={inputCls}
              />
            </Field>
            <Field label="API key">
              <input
                data-testid="settings-llm-api-key"
                type="password"
                placeholder="unchanged when left as ***"
                value={doc.llm.apiKey ?? ''}
                onChange={(e) =>
                  setDoc({ ...doc, llm: { ...doc.llm, apiKey: e.target.value || null } })
                }
                className={inputCls}
              />
            </Field>
          </Section>

          <Section title="Voice">
            <Field label="STT provider">
              <input
                data-testid="settings-voice-stt"
                type="text"
                value={doc.voice.sttProvider}
                onChange={(e) =>
                  setDoc({ ...doc, voice: { ...doc.voice, sttProvider: e.target.value } })
                }
                className={inputCls}
              />
            </Field>
            <Field label="TTS provider">
              <input
                data-testid="settings-voice-tts"
                type="text"
                value={doc.voice.ttsProvider}
                onChange={(e) =>
                  setDoc({ ...doc, voice: { ...doc.voice, ttsProvider: e.target.value } })
                }
                className={inputCls}
              />
            </Field>
            <Field label="Piper voice path">
              <input
                data-testid="settings-voice-piper-path"
                type="text"
                value={doc.voice.piperVoicePath ?? ''}
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

          <Section title="Shortcuts">
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
            <Field label="Stop-all">
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
          </Section>

          <Section title="Privacy">
            <Toggle
              testId="settings-privacy-telemetry"
              label="Send anonymous usage telemetry"
              checked={doc.privacy.telemetryEnabled}
              onChange={(v) =>
                setDoc({ ...doc, privacy: { ...doc.privacy, telemetryEnabled: v } })
              }
            />
            <Toggle
              testId="settings-privacy-screen-capture"
              label="Allow screen capture for context"
              checked={doc.privacy.allowScreenCapture}
              onChange={(v) =>
                setDoc({ ...doc, privacy: { ...doc.privacy, allowScreenCapture: v } })
              }
            />
            <Toggle
              testId="settings-privacy-local-only"
              label="Local-only mode (no network calls)"
              checked={doc.privacy.localOnly}
              onChange={(v) => setDoc({ ...doc, privacy: { ...doc.privacy, localOnly: v } })}
            />
          </Section>

          {error ? (
            <p data-testid="settings-error" className="text-sm text-red-600">
              {error}
            </p>
          ) : null}

          <div className="flex items-center gap-3">
            <button
              type="submit"
              data-testid="settings-save"
              disabled={saving}
              className="rounded-md bg-thaddeus-ink px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
            >
              {saving ? 'Saving…' : 'Save'}
            </button>
            {savedAt ? (
              <span data-testid="settings-saved" className="text-xs text-emerald-700">
                Saved at {savedAt}
              </span>
            ) : null}
          </div>
        </form>
      )}
    </PageScaffold>
  );
}

const inputCls =
  'w-full rounded-md border border-slate-300 px-3 py-1.5 text-sm focus:border-thaddeus-ink focus:outline-none';

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <fieldset className="space-y-3">
      <legend className="text-sm font-semibold text-thaddeus-ink">{title}</legend>
      {children}
    </fieldset>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-slate-600">{label}</span>
      {children}
    </label>
  );
}

function Toggle({
  testId,
  label,
  checked,
  onChange,
}: {
  testId: string;
  label: string;
  checked: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <label className="flex items-center gap-2 text-sm text-slate-700">
      <input
        type="checkbox"
        data-testid={testId}
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
      />
      {label}
    </label>
  );
}
