import { createRootRoute, Link, Outlet } from '@tanstack/react-router';
import { useEffect } from 'react';
import {
  Activity,
  BookOpenText,
  ClipboardList,
  Cog,
  Database,
  Gauge,
  History,
  Home,
  Library,
  MessageSquareText,
  type LucideIcon,
} from 'lucide-react';
import { useRuntimeStore } from '../stores/runtimeStore';
import { usePermissionsStore } from '../stores/permissionsStore';
import { useToolActivityStore } from '../stores/toolActivityStore';
import { RuntimeStateBadge } from '../components/RuntimeStateBadge';
import { KillAppButton } from '../components/KillAppButton';
import { PermissionModal } from '../components/PermissionModal';
import { readRuntimeMetadata } from '../lib/runtime';

export const Route = createRootRoute({
  component: RootLayout,
});

interface NavEntry {
  to: string;
  label: string;
  icon: LucideIcon;
}

const primaryNav: ReadonlyArray<NavEntry> = [
  { to: '/', label: 'Home', icon: Home },
  { to: '/chat', label: 'Chat', icon: MessageSquareText },
  { to: '/wiki', label: 'Wiki', icon: Library },
  { to: '/history', label: 'History', icon: History },
  { to: '/activity', label: 'Activity', icon: Activity },
  { to: '/modules', label: 'Data', icon: Database },
];

const secondaryNav: ReadonlyArray<NavEntry> = [
  { to: '/memory', label: 'Memory', icon: BookOpenText },
  { to: '/routines', label: 'Routines', icon: ClipboardList },
  { to: '/settings', label: 'Settings', icon: Cog },
  { to: '/diagnostics', label: 'Diagnostics', icon: Gauge },
];

function RootLayout() {
  const connect = useRuntimeStore((s) => s.connect);
  const disconnect = useRuntimeStore((s) => s.disconnect);
  // Kick background stores on mount so they subscribe to WS events before
  // any events they care about start flowing. If we lazily subscribe from
  // feature components, early events (e.g. tool.started firing before the
  // assistant message node exists) are lost to the race.
  const startPermissions = usePermissionsStore((s) => s.start);
  const startToolActivity = useToolActivityStore((s) => s.start);
  const meta = readRuntimeMetadata();
  const versionLabel = meta.version === 'dev' ? 'dev' : `v${meta.version}`;

  useEffect(() => {
    connect();
    startPermissions();
    startToolActivity();
    return () => disconnect();
  }, [connect, disconnect, startPermissions, startToolActivity]);

  return (
    <div className="flex h-full bg-canvas text-ink" data-testid="workspace-root">
      <aside
        className="group/aside hidden w-[68px] shrink-0 flex-col border-r border-line bg-canvas py-4 transition-[width] duration-200 hover:w-56 md:flex"
        aria-label="Workspace"
      >
        <Link
          to="/"
          className="mx-3 mb-5 flex h-9 items-center gap-2.5 text-ink"
          aria-label="Sir Thaddeus home"
        >
          <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-accent text-white">
            <ThaddeusMark />
          </span>
          <span className="pointer-events-none overflow-hidden whitespace-nowrap text-[15px] font-semibold opacity-0 transition-opacity duration-150 group-hover/aside:opacity-100">
            Sir Thaddeus
          </span>
        </Link>

        <NavGroup items={primaryNav} />
        <div className="my-3 mx-3 h-px bg-line" />
        <NavGroup items={secondaryNav} />

        <div className="mt-auto mx-3 pt-4 text-[11px] text-ink-subtle">
          <div className="overflow-hidden whitespace-nowrap opacity-0 transition-opacity duration-150 group-hover/aside:opacity-100">
            <span data-testid="runtime-version">{versionLabel}</span>
            <span className="mx-2 text-ink-subtle/60">·</span>
            <span className="font-mono lowercase tracking-wide">local</span>
          </div>
        </div>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex min-w-0 h-11 items-center justify-between gap-3 overflow-hidden border-b border-line bg-canvas/80 px-4 backdrop-blur md:px-6">
          <nav
            className="flex min-w-0 flex-1 items-center gap-1 overflow-x-auto whitespace-nowrap md:hidden"
            aria-label="Primary"
          >
            {primaryNav.concat(secondaryNav).map(({ to, label }) => (
              <Link
                key={to}
                to={to}
                activeProps={{ className: 'text-ink' }}
                className="shrink-0 rounded-full px-2.5 py-1 text-xs text-ink-muted hover:text-ink"
              >
                {label}
              </Link>
            ))}
          </nav>
          <div className="hidden md:block" />
          <div className="flex items-center gap-3 text-xs text-ink-muted">
            <KillAppButton />
            <RuntimeStateBadge />
          </div>
        </header>

        <main className="flex-1 overflow-y-auto">
          <Outlet />
        </main>
      </div>

      {/* Global tool-permission prompt. Renders nothing until the runtime
          asks for approval; shows the head of the queue when it fires. */}
      <PermissionModal />
    </div>
  );
}

function NavGroup({ items }: { items: ReadonlyArray<NavEntry> }) {
  return (
    <ul className="px-2 space-y-0.5">
      {items.map(({ to, label, icon: Icon }) => (
        <li key={to}>
          <Link
            to={to}
            activeProps={{
              className: 'bg-accent-soft text-ink',
            }}
            activeOptions={{ exact: to === '/' }}
            className="flex h-9 items-center gap-3 rounded-xl px-3 text-sm text-ink-muted transition-colors hover:text-ink"
          >
            <Icon className="h-[18px] w-[18px] shrink-0" strokeWidth={1.75} />
            <span className="pointer-events-none overflow-hidden whitespace-nowrap opacity-0 transition-opacity duration-150 group-hover/aside:opacity-100">
              {label}
            </span>
          </Link>
        </li>
      ))}
    </ul>
  );
}

function ThaddeusMark() {
  // Tiny 4-point spark — geometric and calm. Sits well inside a rounded square.
  return (
    <svg
      viewBox="0 0 24 24"
      fill="currentColor"
      className="h-4 w-4"
      aria-hidden
    >
      <path d="M12 3.5l1.7 5 5 1.7-5 1.7L12 17l-1.7-5.1-5-1.7 5-1.7z" />
    </svg>
  );
}
