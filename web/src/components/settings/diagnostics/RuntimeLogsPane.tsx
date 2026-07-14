import type { RuntimeLogResponse, RuntimeLogSummary } from '@thaddeus/shared-types';
import { formatAbsoluteTime, formatBytes, formatRelativeTime } from './logFormatting';

export function RuntimeLogsPane({
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
