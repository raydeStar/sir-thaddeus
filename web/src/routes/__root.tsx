import {
  createRootRoute,
  Link,
  Outlet,
  useNavigate,
} from '@tanstack/react-router';
import { useEffect } from 'react';
import { BookOpen, ClipboardList, Globe, Home, MessageSquareText, ShieldCheck, WifiOff } from 'lucide-react';
import { useRuntimeStore } from '../stores/runtimeStore';
import { usePermissionsStore } from '../stores/permissionsStore';
import { useToolActivityStore } from '../stores/toolActivityStore';
import { useBoundaryStore } from '../stores/boundaryStore';
import { RuntimeStateBadge } from '../components/RuntimeStateBadge';
import { KillAppButton } from '../components/KillAppButton';
import { PermissionPauseCard } from '../components/PermissionModal';
import { ThaddeusSignet } from '../components/ThaddeusSignet';
import { WorkbenchSidebar } from '../components/WorkbenchSidebar';
import { WikiWorkbench } from '../components/WikiWorkbench';
import { CommandPalette } from '../components/CommandPalette';
import { readRuntimeMetadata } from '../lib/runtime';

export const Route = createRootRoute({
  component: RootLayout,
});

function RootLayout() {
  const connect = useRuntimeStore((state) => state.connect);
  const disconnect = useRuntimeStore((state) => state.disconnect);
  const startPermissions = usePermissionsStore((state) => state.start);
  const startToolActivity = useToolActivityStore((state) => state.start);
  const pendingPermissions = usePermissionsStore((state) => state.queue.length);
  const sessionGrants = usePermissionsStore((state) => state.sessionGrants);
  const refreshSessionGrants = usePermissionsStore((state) => state.refreshSessionGrants);
  const connected = useRuntimeStore((state) => state.connected);
  const posture = useBoundaryStore((state) => state.posture);
  const refreshBoundary = useBoundaryStore((state) => state.refresh);
  const pathname = typeof window === 'undefined' ? '/' : window.location.pathname;
  const navigate = useNavigate();
  const meta = readRuntimeMetadata();
  const versionLabel = meta.version === 'dev' ? 'dev' : `v${meta.version}`;
  const insideConversation = /^\/chat\/[^/]+/.test(pathname);

  useEffect(() => {
    connect();
    startPermissions();
    startToolActivity();
    return () => disconnect();
  }, [connect, disconnect, startPermissions, startToolActivity]);

  // Re-read the boundary posture whenever the runtime connects, so the header
  // reflects persisted privacy settings instead of a hardcoded assumption.
  useEffect(() => {
    if (!connected) return;
    void refreshBoundary();
    void refreshSessionGrants();
  }, [connected, refreshBoundary, refreshSessionGrants]);

  useEffect(() => {
    const handler = (event: KeyboardEvent) => {
      if (event.key.toLowerCase() !== 'n' || !event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
      const target = event.target as HTMLElement | null;
      if (target?.matches('input, textarea, [contenteditable="true"]')) return;
      event.preventDefault();
      void navigate({ to: '/' });
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [navigate]);

  return (
    <div className="workspace-shell flex h-full text-ink" data-testid="workspace-root">
      <WorkbenchSidebar versionLabel={versionLabel} />

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="shell-header flex h-12 min-w-0 shrink-0 items-center justify-between gap-2 overflow-hidden border-b border-line px-3 backdrop-blur-xl md:px-4">
          <Link to="/" className="flex h-8 w-8 shrink-0 items-center justify-center lg:hidden" aria-label="Sir Thaddeus home">
            <ThaddeusSignet className="h-7 w-7" />
          </Link>
          <nav className="flex min-w-0 flex-1 items-center gap-1 overflow-x-auto whitespace-nowrap lg:hidden" aria-label="Primary">
            <MobileLink to="/" label="Home" icon={Home} />
            <MobileLink to="/chat" label="Chat" icon={MessageSquareText} />
            <MobileLink to="/wiki" label="Wiki" icon={BookOpen} />
            <MobileLink to="/routines" label="Routines" icon={ClipboardList} />
          </nav>

          <div className="hidden min-w-0 items-center gap-2 text-[10px] text-ink-subtle lg:flex">
            {/* Boundary posture. State is signalled by icon + text as well as
                colour so it survives a monochrome or colour-blind read. */}
            <span
              className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-2.5 py-1"
              data-testid="boundary-posture"
              title={boundaryPostureHint(posture)}
            >
              {posture === 'offline' ? (
                <WifiOff className="h-3 w-3 text-emerald-600 dark:text-emerald-400" aria-hidden />
              ) : posture === 'web-allowed' ? (
                <Globe className="h-3 w-3 text-ink-subtle" aria-hidden />
              ) : (
                <span className="h-1.5 w-1.5 rounded-full bg-ink-subtle" aria-hidden />
              )}
              {posture === 'offline'
                ? 'Local · Offline'
                : posture === 'web-allowed'
                  ? 'Local · Web allowed'
                  : 'Local'}
            </span>
            {pendingPermissions > 0 ? (
              <span className="inline-flex items-center gap-1.5 rounded-full border border-amber-500/30 bg-amber-500/10 px-2.5 py-1 text-amber-700 dark:text-amber-300">
                <ShieldCheck className="h-3 w-3" />
                {pendingPermissions} permission {pendingPermissions === 1 ? 'waiting' : 'requests waiting'}
              </span>
            ) : sessionGrants.length > 0 ? (
              /* Standing grants are posture, not an alert — quiet styling, and
                 only shown when nothing is actively waiting on the user. */
              <Link
                to="/settings"
                search={{ tab: 'permissions' }}
                className="inline-flex items-center gap-1.5 rounded-full border border-line px-2.5 py-1 text-ink-muted transition-colors hover:text-ink"
                title={`Active session grants:\n${sessionGrants.join('\n')}`}
              >
                <ShieldCheck className="h-3 w-3" />
                {sessionGrants.length} session {sessionGrants.length === 1 ? 'grant' : 'grants'}
              </Link>
            ) : null}
          </div>

          <div className="flex items-center gap-2 text-xs text-ink-muted">
            <KillAppButton />
            <RuntimeStateBadge />
          </div>
        </header>

        <div className="flex min-h-0 min-w-0 flex-1">
          <main className="min-w-0 flex-1 overflow-y-auto">
            <Outlet />
            {!insideConversation && pendingPermissions > 0 ? (
              <div className="mx-auto w-full max-w-3xl px-4 pb-8">
                <PermissionPauseCard compact />
              </div>
            ) : null}
          </main>
          <WikiWorkbench />
        </div>
      </div>

      <div className="sr-only" aria-live="polite" aria-atomic="true" id="app-status-live-region" />
      <div className="sr-only" aria-live="assertive" aria-atomic="true">
        {pendingPermissions > 0 ? `${pendingPermissions} permission request waiting.` : ''}
      </div>
      <CommandPalette />
    </div>
  );
}

function boundaryPostureHint(posture: 'unknown' | 'offline' | 'web-allowed'): string {
  if (posture === 'offline') return 'Offline mode: web-backed tools are blocked. Nothing leaves this machine.';
  if (posture === 'web-allowed') return 'Web-backed tools may run, but only with an explicit permission grant.';
  return 'Boundary posture not read yet.';
}

function MobileLink({
  to,
  label,
  icon: Icon,
}: {
  to: string;
  label: string;
  icon: typeof Home;
}) {
  return (
    <Link
      to={to}
      activeProps={{ className: 'bg-accent-soft text-accent' }}
      activeOptions={{ exact: to === '/' }}
      className="inline-flex shrink-0 items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium text-ink-muted transition-colors hover:bg-canvas-sunken hover:text-ink"
    >
      <Icon className="h-3.5 w-3.5" />
      {label}
    </Link>
  );
}
