import { createFileRoute } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { PageScaffold } from '../components/PageScaffold';
import { getDiagnostics } from '../lib/activityApi';
import type { DiagnosticsResponse } from '@thaddeus/shared-types';

export const Route = createFileRoute('/diagnostics')({
  component: DiagnosticsRoute,
});

function DiagnosticsRoute() {
  const [data, setData] = useState<DiagnosticsResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    let cancelled = false;
    getDiagnostics()
      .then((d) => {
        if (!cancelled) setData(d);
      })
      .catch((e: Error) => {
        if (!cancelled) setError(e.message);
      });
    return () => {
      cancelled = true;
    };
  }, [tick]);

  return (
    <PageScaffold
      testId="route-diagnostics"
      title="Diagnostics"
      subtitle="Local runtime status."
    >
      <div className="mb-4 flex items-center justify-end">
        <button
          type="button"
          data-testid="diagnostics-refresh"
          onClick={() => setTick((n) => n + 1)}
          className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3.5 py-1.5 text-sm font-medium text-ink shadow-soft transition hover:bg-accent-soft"
        >
          <RefreshCw className="h-4 w-4" strokeWidth={1.75} />
          Refresh
        </button>
      </div>

      {error ? (
        <p className="text-sm text-rose-500" data-testid="diagnostics-error">
          {error}
        </p>
      ) : !data ? (
        <p className="text-sm italic text-ink-subtle" data-testid="diagnostics-loading">
          Loading…
        </p>
      ) : (
        <dl
          data-testid="diagnostics-detail"
          className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-2 text-sm"
        >
          <Row label="State" value={data.state} testId="diagnostics-state" />
          <Row
            label="Uptime"
            value={formatUptime(data.uptimeSeconds)}
            testId="diagnostics-uptime"
          />
          <Row
            label="Threads"
            value={String(data.threadCount)}
            testId="diagnostics-thread-count"
          />
          <Row
            label="Voice"
            value={formatVoiceStatus(data)}
            testId="diagnostics-voice"
          />
          {data.voice ? (
            <>
              <Row
                label="Voice host"
                value={data.voice.hostReachable ? 'reachable' : data.voice.status}
                testId="diagnostics-voice-host"
              />
              <Row
                label="Voice input"
                value={data.voice.inputAvailable ? 'available' : data.voice.asrReady ? 'warming' : 'unavailable'}
                testId="diagnostics-voice-input"
              />
              <Row
                label="Voice output"
                value={data.voice.outputAvailable ? 'available' : data.voice.ttsReady ? 'warming' : 'unavailable'}
                testId="diagnostics-voice-output"
              />
              <Row
                label="Voice detail"
                value={data.voice.message}
                testId="diagnostics-voice-detail"
              />
            </>
          ) : null}
          <Row label="Build" value={data.buildVersion} testId="diagnostics-build" />
          <Row label="PID" value={String(data.pid)} testId="diagnostics-pid" />
          <Row label="Thread store" value={data.threadStoreRoot} testId="diagnostics-store" />
          {data.logsRoot ? (
            <Row label="Logs" value={data.logsRoot} testId="diagnostics-logs" />
          ) : null}
          {data.turnsRoot ? (
            <Row label="Turn traces" value={data.turnsRoot} testId="diagnostics-turns" />
          ) : null}
        </dl>
      )}
    </PageScaffold>
  );
}

function Row({ label, value, testId }: { label: string; value: string; testId: string }) {
  return (
    <>
      <dt className="font-medium text-ink">{label}</dt>
      <dd data-testid={testId} className="text-ink-muted break-all">
        {value}
      </dd>
    </>
  );
}

function formatVoiceStatus(data: DiagnosticsResponse): string {
  if (!data.voice) return data.voiceAvailable ? 'available' : 'unavailable';
  if (!data.voice.voiceHostEnabled) return 'disabled';
  if (data.voice.inputAvailable && data.voice.outputAvailable) return 'available';
  if (data.voice.inputAvailable) return 'input available';
  return data.voice.status || 'unavailable';
}

function formatUptime(seconds: number): string {
  const s = Math.floor(seconds);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  if (h > 0) return `${h}h ${m}m ${sec}s`;
  if (m > 0) return `${m}m ${sec}s`;
  return `${sec}s`;
}
