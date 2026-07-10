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
      className="flex h-7 w-7 items-center justify-center rounded-full border border-red-500/25 bg-canvas-raised text-red-500 shadow-soft transition hover:border-red-500/60 hover:bg-red-500 hover:text-white focus:outline-none focus:ring-2 focus:ring-red-500/35 disabled:cursor-not-allowed disabled:opacity-50"
    >
      <Power className="h-3.5 w-3.5" strokeWidth={2.5} aria-hidden />
    </button>
  );
}
