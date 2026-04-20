import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { getSettings, putSettings } from '../lib/settingsApi';
import type { SettingsDocument } from '@thaddeus/shared-types';

export const Route = createFileRoute('/onboarding')({
  component: OnboardingRoute,
});

const STEPS = ['welcome', 'privacy', 'voice', 'done'] as const;
type Step = (typeof STEPS)[number];

function OnboardingRoute() {
  const navigate = useNavigate();
  const [doc, setDoc] = useState<SettingsDocument | null>(null);
  const [step, setStep] = useState<Step>('welcome');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getSettings()
      .then(setDoc)
      .catch((e: Error) => setError(e.message));
  }, []);

  const next = () => {
    const i = STEPS.indexOf(step);
    if (i < STEPS.length - 1) setStep(STEPS[i + 1]);
  };
  const back = () => {
    const i = STEPS.indexOf(step);
    if (i > 0) setStep(STEPS[i - 1]);
  };

  const finish = async () => {
    if (!doc || busy) return;
    setBusy(true);
    setError(null);
    try {
      const updated: SettingsDocument = {
        ...doc,
        flags: { ...doc.flags, onboardingCompleted: true },
      };
      await putSettings(updated);
      void navigate({ to: '/' });
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  };

  return (
    <PageScaffold testId="route-onboarding" title="Welcome" subtitle="A short tour before you start.">
      {!doc ? (
        <p data-testid="onboarding-loading" className="text-sm italic text-slate-500">
          Loading…
        </p>
      ) : (
        <div data-testid={`onboarding-step-${step}`} className="space-y-4">
          {step === 'welcome' && (
            <>
              <h2 className="text-base font-semibold text-thaddeus-ink">Hello.</h2>
              <p className="text-sm text-slate-700">
                Sir Thaddeus is a local-first agent. Your conversations live on your machine; no
                cloud account is required.
              </p>
            </>
          )}
          {step === 'privacy' && (
            <>
              <h2 className="text-base font-semibold text-thaddeus-ink">Privacy</h2>
              <p className="text-sm text-slate-700">
                By default, telemetry is off and screen capture is off. You can change these any
                time in Settings &rarr; Privacy.
              </p>
              <ul className="list-disc pl-5 text-sm text-slate-700">
                <li>Telemetry: {doc.privacy.telemetryEnabled ? 'ON' : 'off'}</li>
                <li>Screen capture: {doc.privacy.allowScreenCapture ? 'ON' : 'off'}</li>
                <li>Local-only mode: {doc.privacy.localOnly ? 'ON' : 'off'}</li>
              </ul>
            </>
          )}
          {step === 'voice' && (
            <>
              <h2 className="text-base font-semibold text-thaddeus-ink">Voice</h2>
              <p className="text-sm text-slate-700">
                Push-to-talk is bound to <strong>{doc.shortcuts.pushToTalk}</strong>. Stop-all is{' '}
                <strong>{doc.shortcuts.stopAll}</strong>. Adjust either in Settings.
              </p>
            </>
          )}
          {step === 'done' && (
            <>
              <h2 className="text-base font-semibold text-thaddeus-ink">All set</h2>
              <p className="text-sm text-slate-700">
                Click Finish to mark onboarding complete and jump into the workspace.
              </p>
            </>
          )}

          {error ? (
            <p data-testid="onboarding-error" className="text-sm text-red-600">
              {error}
            </p>
          ) : null}

          <div className="flex items-center gap-2">
            <button
              type="button"
              data-testid="onboarding-back"
              onClick={back}
              disabled={step === 'welcome' || busy}
              className="rounded-md border border-slate-300 px-3 py-1.5 text-sm disabled:opacity-50"
            >
              Back
            </button>
            {step !== 'done' ? (
              <button
                type="button"
                data-testid="onboarding-next"
                onClick={next}
                className="rounded-md bg-thaddeus-ink px-3 py-1.5 text-sm font-medium text-white"
              >
                Next
              </button>
            ) : (
              <button
                type="button"
                data-testid="onboarding-finish"
                onClick={() => void finish()}
                disabled={busy}
                className="rounded-md bg-thaddeus-ink px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50"
              >
                {busy ? 'Saving…' : 'Finish'}
              </button>
            )}
            <span className="ml-auto text-xs text-slate-500" data-testid="onboarding-progress">
              Step {STEPS.indexOf(step) + 1} of {STEPS.length}
            </span>
          </div>
        </div>
      )}
    </PageScaffold>
  );
}
