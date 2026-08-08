import { createFileRoute } from '@tanstack/react-router';
import { RuntimeStateBadge } from '../components/RuntimeStateBadge';
import { KillAppButton } from '../components/KillAppButton';

export const Route = createFileRoute('/compact')({
  component: CompactRoute,
});

/**
 * Quick-interaction compact panel surface (per spec §11). Phase 1 ships only the
 * idle pill; the full transcript stream and PTT controls land in Phase 2.
 */
function CompactRoute() {
  return (
    <section data-testid="route-compact" className="flex h-full items-center justify-center bg-canvas-sunken p-4">
      <div className="w-full max-w-md rounded-2xl border border-line bg-canvas-raised p-6">
        <div className="flex items-center justify-between">
          <p className="text-sm font-medium text-ink">Sir Thaddeus</p>
          <div className="flex items-center gap-2">
            <KillAppButton />
            <RuntimeStateBadge />
          </div>
        </div>
        <p className="mt-4 text-xs text-ink-muted">Press your global shortcut to start a quick interaction.</p>
      </div>
    </section>
  );
}
