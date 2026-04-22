import { useMemo, useState, useEffect } from 'react';
import cronstrue from 'cronstrue';
import type { AutomationSchedule } from '@thaddeus/shared-types';

interface SchedulePickerProps {
  value: AutomationSchedule | null | undefined;
  onChange: (schedule: AutomationSchedule) => void;
  testIdPrefix?: string;
}

type Preset =
  | 'off'
  | 'every-15m'
  | 'every-hour'
  | 'daily'
  | 'weekdays'
  | 'weekly'
  | 'monthly'
  | 'one-shot'
  | 'custom';

const presetOptions: { value: Preset; label: string }[] = [
  { value: 'off', label: 'Off — run only when I click Run' },
  { value: 'every-15m', label: 'Every 15 minutes' },
  { value: 'every-hour', label: 'Every hour (top of the hour)' },
  { value: 'daily', label: 'Every day at…' },
  { value: 'weekdays', label: 'Every weekday at…' },
  { value: 'weekly', label: 'Weekly on… at…' },
  { value: 'monthly', label: 'Monthly on day N at…' },
  { value: 'one-shot', label: 'One time at…' },
  { value: 'custom', label: 'Custom cron…' },
];

const dayNames = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

/** Best-effort inference of which preset a saved cron expression came from. */
function detectPreset(schedule: AutomationSchedule | null | undefined): Preset {
  if (!schedule || schedule.kind === 'off') return 'off';
  if (schedule.kind === 'one-shot') return 'one-shot';
  const c = (schedule.cron ?? '').trim();
  if (!c) return 'custom';
  if (c === '*/15 * * * *') return 'every-15m';
  if (c === '0 * * * *') return 'every-hour';
  // 0 9 * * * (daily at hh:mm)
  if (/^\d+ \d+ \* \* \*$/.test(c)) return 'daily';
  // 15 8 * * 1-5
  if (/^\d+ \d+ \* \* 1-5$/.test(c)) return 'weekdays';
  // 0 9 * * 1 (weekly)
  if (/^\d+ \d+ \* \* [0-6]$/.test(c)) return 'weekly';
  // 0 9 5 * * (monthly)
  if (/^\d+ \d+ \d+ \* \*$/.test(c)) return 'monthly';
  return 'custom';
}

/** Build a cron expression from preset + time + day selectors. */
function buildCron(preset: Preset, hh: number, mm: number, dow: number, dom: number): string {
  switch (preset) {
    case 'every-15m': return '*/15 * * * *';
    case 'every-hour': return '0 * * * *';
    case 'daily':     return `${mm} ${hh} * * *`;
    case 'weekdays':  return `${mm} ${hh} * * 1-5`;
    case 'weekly':    return `${mm} ${hh} * * ${dow}`;
    case 'monthly':   return `${mm} ${hh} ${dom} * *`;
    default:          return '0 9 * * *';
  }
}

function tryHumanize(cron: string | null | undefined): string | null {
  if (!cron) return null;
  try {
    return cronstrue.toString(cron, { use24HourTimeFormat: false, throwExceptionOnParseError: true });
  } catch {
    return null;
  }
}

function formatNextRun(iso: string | null | undefined): string | null {
  if (!iso) return null;
  try {
    const d = new Date(iso);
    const now = Date.now();
    const diffMs = d.getTime() - now;
    const diffMin = Math.round(diffMs / 60_000);
    if (diffMin < -1) return `(past) ${d.toLocaleString()}`;
    if (diffMin < 1) return `any second now · ${d.toLocaleTimeString()}`;
    if (diffMin < 60) return `in ${diffMin} min · ${d.toLocaleTimeString()}`;
    const diffHr = Math.round(diffMin / 60);
    if (diffHr < 24) return `in ${diffHr}h · ${d.toLocaleString()}`;
    return d.toLocaleString();
  } catch {
    return iso;
  }
}

/**
 * Schedule picker for automations. Presets drive a cron expression under
 * the hood; custom cron is an expert escape hatch. "One time at" uses
 * {@link AutomationSchedule.runAt} instead of a cron — this is a small
 * bifurcation but lets users set reminders without learning cron syntax.
 */
export function SchedulePicker({ value, onChange, testIdPrefix = 'automation-schedule' }: SchedulePickerProps) {
  const initialPreset = useMemo(() => detectPreset(value), [value]);
  const [preset, setPreset] = useState<Preset>(initialPreset);

  // Local preset-field state. Initialized from the current cron when the
  // component mounts; preserved so users can toggle preset without losing
  // their time selection.
  const [hh, setHh] = useState(9);
  const [mm, setMm] = useState(0);
  const [dow, setDow] = useState(1); // Mon
  const [dom, setDom] = useState(1);
  const [customCron, setCustomCron] = useState(value?.cron ?? '0 9 * * *');
  const [runAtLocal, setRunAtLocal] = useState<string>(''); // datetime-local value

  // Seed fields from an existing cron once on mount so edit-mode doesn't
  // reset the user's carefully-picked time when they re-open the page.
  useEffect(() => {
    const c = (value?.cron ?? '').trim();
    const parts = c.split(/\s+/);
    if (parts.length === 5) {
      const pmm = Number.parseInt(parts[0], 10);
      const phh = Number.parseInt(parts[1], 10);
      if (Number.isFinite(pmm)) setMm(pmm);
      if (Number.isFinite(phh)) setHh(phh);
      if (/^\d+$/.test(parts[4])) setDow(Number.parseInt(parts[4], 10));
      if (/^\d+$/.test(parts[2])) setDom(Number.parseInt(parts[2], 10));
    }
    if (value?.runAt) {
      const d = new Date(value.runAt);
      // datetime-local expects "YYYY-MM-DDTHH:mm" in local time
      const pad = (n: number) => String(n).padStart(2, '0');
      setRunAtLocal(
        `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
      );
    }
    if (c && !value?.cron) setCustomCron(c);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const emit = (
    nextPreset: Preset,
    overrides?: { hh?: number; mm?: number; dow?: number; dom?: number; customCron?: string; runAtLocal?: string },
  ) => {
    const h = overrides?.hh ?? hh;
    const m = overrides?.mm ?? mm;
    const d = overrides?.dow ?? dow;
    const dm = overrides?.dom ?? dom;
    const cc = overrides?.customCron ?? customCron;
    const ral = overrides?.runAtLocal ?? runAtLocal;

    if (nextPreset === 'off') {
      onChange({ kind: 'off', cron: null, runAt: null, timezone: value?.timezone ?? null });
      return;
    }
    if (nextPreset === 'one-shot') {
      const iso = ral ? new Date(ral).toISOString() : null;
      onChange({
        kind: 'one-shot', cron: null, runAt: iso,
        timezone: value?.timezone ?? null,
      });
      return;
    }
    const cron = nextPreset === 'custom' ? cc : buildCron(nextPreset, h, m, d, dm);
    onChange({
      kind: 'cron', cron, runAt: null,
      timezone: value?.timezone ?? null,
    });
  };

  const humanized = preset === 'one-shot'
    ? (runAtLocal ? `Once at ${new Date(runAtLocal).toLocaleString()}` : 'Pick a date and time below')
    : tryHumanize(preset === 'custom' ? customCron : buildCron(preset, hh, mm, dow, dom));

  const nextRun = formatNextRun(value?.nextRunAt);

  return (
    <div className="space-y-3" data-testid={testIdPrefix}>
      <div>
        <p className="text-[13px] font-medium text-ink">Schedule</p>
        <p className="mt-0.5 text-[12px] text-ink-muted">
          How often should Sir Thaddeus run this on his own?
        </p>
      </div>

      <select
        data-testid={`${testIdPrefix}-preset`}
        value={preset}
        onChange={(e) => {
          const p = e.target.value as Preset;
          setPreset(p);
          emit(p);
        }}
        className="block w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 text-sm text-ink focus:border-accent-ring focus:outline-none focus:ring-2 focus:ring-accent/20"
      >
        {presetOptions.map((o) => (
          <option key={o.value} value={o.value}>{o.label}</option>
        ))}
      </select>

      {/* Time pickers for daily / weekdays / weekly / monthly */}
      {(preset === 'daily' || preset === 'weekdays' || preset === 'weekly' || preset === 'monthly') ? (
        <div className="flex flex-wrap items-center gap-2 text-[13px]">
          <span className="text-ink-muted">at</span>
          <input
            type="number" min={0} max={23}
            data-testid={`${testIdPrefix}-hour`}
            value={hh}
            onChange={(e) => {
              const v = Math.max(0, Math.min(23, Number.parseInt(e.target.value || '0', 10)));
              setHh(v);
              emit(preset, { hh: v });
            }}
            className="w-16 rounded-lg border border-line bg-canvas-raised px-2 py-1 text-center text-sm"
          />
          <span className="text-ink-muted">:</span>
          <input
            type="number" min={0} max={59}
            data-testid={`${testIdPrefix}-minute`}
            value={mm}
            onChange={(e) => {
              const v = Math.max(0, Math.min(59, Number.parseInt(e.target.value || '0', 10)));
              setMm(v);
              emit(preset, { mm: v });
            }}
            className="w-16 rounded-lg border border-line bg-canvas-raised px-2 py-1 text-center text-sm"
          />
          <span className="text-[11px] text-ink-subtle">24h · local time</span>

          {preset === 'weekly' ? (
            <>
              <span className="ml-3 text-ink-muted">on</span>
              <select
                data-testid={`${testIdPrefix}-dow`}
                value={dow}
                onChange={(e) => {
                  const v = Number.parseInt(e.target.value, 10);
                  setDow(v);
                  emit(preset, { dow: v });
                }}
                className="rounded-lg border border-line bg-canvas-raised px-2 py-1 text-sm"
              >
                {dayNames.map((n, i) => <option key={n} value={i}>{n}</option>)}
              </select>
            </>
          ) : null}

          {preset === 'monthly' ? (
            <>
              <span className="ml-3 text-ink-muted">on day</span>
              <input
                type="number" min={1} max={28}
                data-testid={`${testIdPrefix}-dom`}
                value={dom}
                onChange={(e) => {
                  const v = Math.max(1, Math.min(28, Number.parseInt(e.target.value || '1', 10)));
                  setDom(v);
                  emit(preset, { dom: v });
                }}
                className="w-16 rounded-lg border border-line bg-canvas-raised px-2 py-1 text-center text-sm"
              />
            </>
          ) : null}
        </div>
      ) : null}

      {/* One-shot: datetime-local */}
      {preset === 'one-shot' ? (
        <input
          type="datetime-local"
          data-testid={`${testIdPrefix}-run-at`}
          value={runAtLocal}
          onChange={(e) => {
            setRunAtLocal(e.target.value);
            emit('one-shot', { runAtLocal: e.target.value });
          }}
          className="block w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 text-sm text-ink focus:border-accent-ring focus:outline-none focus:ring-2 focus:ring-accent/20"
        />
      ) : null}

      {/* Custom cron */}
      {preset === 'custom' ? (
        <div>
          <input
            type="text"
            data-testid={`${testIdPrefix}-custom`}
            placeholder="min hour dom month dow  (e.g. 15 8 * * 1-5)"
            value={customCron}
            onChange={(e) => {
              setCustomCron(e.target.value);
              emit('custom', { customCron: e.target.value });
            }}
            className="block w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 font-mono text-[13px] text-ink focus:border-accent-ring focus:outline-none focus:ring-2 focus:ring-accent/20"
          />
        </div>
      ) : null}

      {/* Humanized summary + next-run */}
      {preset !== 'off' ? (
        <div className="rounded-xl border border-line bg-canvas-sunken/40 px-3 py-2 text-[12px]">
          <p className="text-ink" data-testid={`${testIdPrefix}-humanized`}>
            {humanized ?? 'Invalid schedule'}
          </p>
          {nextRun ? (
            <p className="mt-0.5 text-ink-muted" data-testid={`${testIdPrefix}-next-run`}>
              Next run {nextRun}
            </p>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
