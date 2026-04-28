import { useState } from 'react';
import { Power } from 'lucide-react';
import { killApp } from '../lib/runtimeActions';

/**
 * Compact red kill switch that lives next to the runtime state badge in the
 * global header. Clicking it stops sidecars and tears the runtime down; the
 * shell supervisor then closes the workspace window so the whole app exits.
 */
export function KillAppButton() {
  const [busy, setBusy] = useState(false);

  const onClick = async () => {
    if (busy) return;
    setBusy(true);
    try {
      await killApp();
    } finally {
      // Window is about to close anyway. Reset state defensively in case the
      // runtime call failed and the user wants to retry.
      setBusy(false);
    }
  };

  return (
    <button
      type="button"
      onClick={onClick}
      disabled={busy}
      data-testid="header-kill-app"
      title="Stop everything and exit Sir Thaddeus"
      aria-label="Stop everything and exit Sir Thaddeus"
      className="flex h-6 w-6 items-center justify-center rounded-full bg-red-600 text-white shadow-sm transition hover:bg-red-700 focus:outline-none focus:ring-2 focus:ring-red-500/40 disabled:cursor-not-allowed disabled:bg-red-900/60"
    >
      <Power className="h-3.5 w-3.5" strokeWidth={2.5} aria-hidden />
    </button>
  );
}
