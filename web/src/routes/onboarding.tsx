import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { Check, Plus, X } from 'lucide-react';
import { PageScaffold } from '../components/PageScaffold';
import { ThaddeusSignet } from '../components/ThaddeusSignet';
import { getSettings, putSettings } from '../lib/settingsApi';
import { getFolderSuggestions, type FolderSuggestion } from '../lib/filesApi';
import type { SettingsDocument } from '@thaddeus/shared-types';

export const Route = createFileRoute('/onboarding')({
  component: OnboardingRoute,
});

const STEPS = ['welcome', 'privacy', 'folders', 'voice', 'done'] as const;
type Step = (typeof STEPS)[number];

function OnboardingRoute() {
  const navigate = useNavigate();
  const [doc, setDoc] = useState<SettingsDocument | null>(null);
  const [step, setStep] = useState<Step>('welcome');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Folder-step state. Loaded lazily — the suggestions endpoint resolves
  // the real OS folder paths the runtime can see, then we present them as
  // checkboxes seeded from what the user already has authorized.
  const [suggestions, setSuggestions] = useState<FolderSuggestion[] | null>(null);
  const [selected, setSelected] = useState<Record<string, boolean>>({});
  const [customRoots, setCustomRoots] = useState<string[]>([]);
  const [customDraft, setCustomDraft] = useState('');

  useEffect(() => {
    getSettings()
      .then(setDoc)
      .catch((e: Error) => setError(e.message));
  }, []);

  useEffect(() => {
    if (suggestions !== null || !doc) return;
    let cancelled = false;
    getFolderSuggestions()
      .then((items) => {
        if (cancelled) return;
        // Seed selection from current AllowedRoots so users re-running
        // onboarding see what's already authorized (case-insensitive on
        // Windows; .NET stores absolute paths normalized either way).
        const existing = new Set((doc.files?.allowedRoots ?? []).map((p) => p.toLowerCase()));
        const seed: Record<string, boolean> = {};
        for (const item of items) {
          seed[item.id] = item.defaultEnabled || existing.has(item.path.toLowerCase());
        }
        const suggestedPaths = new Set(items.map((s) => s.path.toLowerCase()));
        const custom = (doc.files?.allowedRoots ?? []).filter(
          (p) => !suggestedPaths.has(p.toLowerCase()),
        );
        setSuggestions(items);
        setSelected(seed);
        setCustomRoots(custom);
      })
      .catch((e: Error) => {
        if (!cancelled) setError(e.message);
      });
    return () => {
      cancelled = true;
    };
  }, [doc, suggestions]);

  const toggleSuggestion = (id: string) => {
    setSelected((current) => ({ ...current, [id]: !current[id] }));
  };

  const addCustomRoot = () => {
    const trimmed = customDraft.trim();
    if (!trimmed) return;
    // Same shape check as Settings → Files: absolute paths only. Keeps the
    // onboarding step honest about what AllowedRoots will accept.
    if (!/^([a-zA-Z]:[\\/])|^[\\/]|^\\\\/.test(trimmed)) {
      setError('Please enter an absolute path (e.g. C:\\Users\\Me\\Projects).');
      return;
    }
    if (customRoots.some((p) => p.toLowerCase() === trimmed.toLowerCase())) {
      setError('That folder is already in the list.');
      return;
    }
    setCustomRoots((current) => [...current, trimmed]);
    setCustomDraft('');
    setError(null);
  };

  const removeCustomRoot = (path: string) => {
    setCustomRoots((current) => current.filter((p) => p !== path));
  };

  const collectAllowedRoots = (): string[] => {
    const out: string[] = [];
    for (const item of suggestions ?? []) {
      if (selected[item.id]) out.push(item.path);
    }
    for (const path of customRoots) out.push(path);
    return out;
  };

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
        files: {
          ...(doc.files ?? { allowedRoots: [], disableAllFileAccess: false, maxDefaultCharsPerRead: 4000 }),
          allowedRoots: collectAllowedRoots(),
        },
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
              <div className="flex items-center gap-3">
                <ThaddeusSignet className="h-12 w-12 shrink-0" />
                <div>
                  <p className="text-[11px] font-medium uppercase tracking-[0.12em] text-accent">
                    Sir Thaddeus
                  </p>
                  <h2 className="mt-0.5 text-base font-semibold text-ink">At your service.</h2>
                </div>
              </div>
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
          {step === 'folders' && (
            <>
              <h2 className="text-base font-semibold text-ink">Folder access</h2>
              <p className="text-sm text-ink">
                Pick the folders the assistant is allowed to <strong>read from</strong>. It cannot
                write, modify, move, or delete anything in these folders — only read what you ask
                it about. You can change this any time in Settings &rarr; Files.
              </p>
              <div
                className="rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-800 dark:text-amber-200"
                data-testid="onboarding-folders-write-notice"
              >
                Writing or editing files is a separate, opt-in feature that's still in development.
                Nothing here grants write access.
              </div>

              {suggestions === null ? (
                <p className="text-xs italic text-ink-muted" data-testid="onboarding-folders-loading">
                  Looking up your folders…
                </p>
              ) : (
                <div className="space-y-2" data-testid="onboarding-folders-suggestions">
                  {suggestions.map((item) => {
                    const checked = !!selected[item.id];
                    const disabled = !item.exists;
                    return (
                      <label
                        key={item.id}
                        data-testid={`onboarding-folder-${item.id}`}
                        className={[
                          'flex cursor-pointer items-start gap-3 rounded-xl border px-3 py-2.5 transition',
                          checked
                            ? 'border-accent bg-accent-soft'
                            : 'border-line bg-canvas-raised/60 hover:border-line-strong',
                          disabled ? 'cursor-not-allowed opacity-60' : '',
                        ].join(' ')}
                      >
                        <input
                          type="checkbox"
                          checked={checked}
                          disabled={disabled}
                          onChange={() => toggleSuggestion(item.id)}
                          className="mt-1 h-4 w-4 accent-accent"
                          aria-label={`Allow assistant to read ${item.label}`}
                        />
                        <div className="min-w-0 flex-1">
                          <div className="flex items-center gap-2 text-sm font-medium text-ink">
                            {item.label}
                            {!item.exists ? (
                              <span className="text-[10px] uppercase tracking-[0.08em] text-ink-subtle">
                                not found
                              </span>
                            ) : null}
                          </div>
                          <div className="text-xs text-ink-muted">{item.description}</div>
                          <div className="mt-0.5 truncate font-mono text-[11px] text-ink-subtle">
                            {item.path}
                          </div>
                        </div>
                      </label>
                    );
                  })}
                </div>
              )}

              <div className="space-y-2" data-testid="onboarding-folders-custom">
                <div className="text-xs font-medium text-ink-muted">Add another folder</div>
                {customRoots.length > 0 ? (
                  <ul className="space-y-1">
                    {customRoots.map((p) => (
                      <li
                        key={p}
                        className="flex items-center justify-between gap-2 rounded-lg border border-line bg-canvas-raised/60 px-3 py-1.5"
                      >
                        <span className="truncate font-mono text-[11px] text-ink">{p}</span>
                        <button
                          type="button"
                          onClick={() => removeCustomRoot(p)}
                          aria-label={`Remove ${p}`}
                          className="inline-flex h-6 w-6 items-center justify-center rounded-full text-ink-muted hover:bg-canvas-sunken hover:text-ink"
                        >
                          <X className="h-3.5 w-3.5" strokeWidth={1.75} />
                        </button>
                      </li>
                    ))}
                  </ul>
                ) : null}
                <div className="flex items-center gap-2">
                  <input
                    type="text"
                    data-testid="onboarding-folder-custom-input"
                    value={customDraft}
                    onChange={(e) => setCustomDraft(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') {
                        e.preventDefault();
                        addCustomRoot();
                      }
                    }}
                    placeholder="C:\Users\Me\Projects"
                    className="flex-1 rounded-lg border border-line bg-canvas-raised/40 px-3 py-1.5 text-sm text-ink placeholder:text-ink-subtle focus:border-accent focus:outline-none"
                  />
                  <button
                    type="button"
                    data-testid="onboarding-folder-custom-add"
                    onClick={addCustomRoot}
                    className="inline-flex items-center gap-1 rounded-lg border border-line bg-canvas-raised px-3 py-1.5 text-xs font-medium text-ink-muted transition hover:border-line-strong hover:text-ink"
                  >
                    <Plus className="h-3.5 w-3.5" strokeWidth={1.75} />
                    Add
                  </button>
                </div>
              </div>

              <p
                className="text-xs text-ink-subtle"
                data-testid="onboarding-folders-summary"
              >
                {collectAllowedRoots().length === 0 ? (
                  <>You haven't picked any folders. That's fine — you can grant access later in Settings.</>
                ) : (
                  <>
                    <Check className="-mt-0.5 mr-1 inline h-3 w-3 text-emerald-500" strokeWidth={2} />
                    {collectAllowedRoots().length} folder{collectAllowedRoots().length === 1 ? '' : 's'} authorized for read-only access.
                  </>
                )}
              </p>
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
