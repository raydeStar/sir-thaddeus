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
        <p data-testid="onboarding-loading" className="text-sm italic text-ink-muted">
          Loading…
        </p>
      ) : (
        <div data-testid={`onboarding-step-${step}`} className="space-y-4">
          {step === 'welcome' && (
            <>
              <h2 className="text-base font-semibold text-ink">Hello.</h2>
              <p className="text-sm text-ink">
                Sir Thaddeus is a local-first agent. Your conversations live on your machine; no
                cloud account is required.
              </p>
            </>
          )}
          {step === 'privacy' && (
            <>
              <h2 className="text-base font-semibold text-ink">Privacy</h2>
              <p className="text-sm text-ink">
                By default, telemetry is off and screen capture is off. You can change these any
                time in Settings &rarr; Privacy.
              </p>
              <ul className="list-disc pl-5 text-sm text-ink">
                <li>Telemetry: {doc.privacy.telemetryEnabled ? 'ON' : 'off'}</li>
                <li>Screen capture: {doc.privacy.allowScreenCapture ? 'ON' : 'off'}</li>
                <li>Local-only mode: {doc.privacy.localOnly ? 'ON' : 'off'}</li>
              </ul>
            </>
          )}
          {step === 'voice' && (
            <>
              <h2 className="text-base font-semibold text-ink">Voice</h2>
              <p className="text-sm text-ink">
                Push-to-talk is bound to <strong>{doc.shortcuts.pushToTalk}</strong>. Stop-all is{' '}
                <strong>{doc.shortcuts.stopAll}</strong>. Adjust either in Settings.
              </p>
            </>
          )}
          {step === 'done' && (
            <>
              <h2 className="text-base font-semibold text-ink">All set</h2>
              <p className="text-sm text-ink">
                Click Finish to mark onboarding complete and jump into the workspace.
              </p>
            </>
          )}

          {error ? (
            <p data-testid="onboarding-error" className="text-sm text-rose-500">
              {error}
            </p>
          ) : null}

          <div className="flex items-center gap-2">
            <button
              type="button"
              data-testid="onboarding-back"
              onClick={back}
              disabled={step === 'welcome' || busy}
              className="rounded-full border border-line px-4 py-2 text-sm text-ink transition-colors hover:bg-accent-soft disabled:opacity-50"
            >
              Back
            </button>
            {step !== 'done' ? (
              <button
                type="button"
                data-testid="onboarding-next"
                onClick={next}
                className="inline-flex items-center gap-1.5 rounded-full bg-accent px-4 py-2 text-sm font-medium text-white transition hover:opacity-90"
              >
                Next
              </button>
            ) : (
              <button
                type="button"
                data-testid="onboarding-finish"
                onClick={() => void finish()}
                disabled={busy}
                className="inline-flex items-center gap-1.5 rounded-full bg-accent px-4 py-2 text-sm font-medium text-white transition hover:opacity-90 disabled:opacity-50"
              >
                {busy ? 'Saving…' : 'Finish'}
              </button>
            )}
            <span className="ml-auto text-xs text-ink-muted" data-testid="onboarding-progress">
              Step {STEPS.indexOf(step) + 1} of {STEPS.length}
            </span>
          </div>
        </div>
      )}
    </PageScaffold>
  );
}
