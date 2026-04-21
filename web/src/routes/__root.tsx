import { createRootRoute, Link, Outlet } from '@tanstack/react-router';
import { useEffect } from 'react';
import {
  Activity,
  BookOpenText,
  Cog,
  Gauge,
  History,
  Home,
  MessageSquareText,
  Sparkles,
  Workflow,
  type LucideIcon,
} from 'lucide-react';
import { useRuntimeStore } from '../stores/runtimeStore';
import { RuntimeStateBadge } from '../components/RuntimeStateBadge';
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
  { to: '/history', label: 'History', icon: History },
  { to: '/activity', label: 'Activity', icon: Activity },
];

const secondaryNav: ReadonlyArray<NavEntry> = [
  { to: '/memory', label: 'Memory', icon: BookOpenText },
  { to: '/automations', label: 'Automations', icon: Workflow },
  { to: '/settings', label: 'Settings', icon: Cog },
  { to: '/diagnostics', label: 'Diagnostics', icon: Gauge },
];

function RootLayout() {
  const connect = useRuntimeStore((s) => s.connect);
  const disconnect = useRuntimeStore((s) => s.disconnect);
  const meta = readRuntimeMetadata();

  useEffect(() => {
    connect();
    return () => disconnect();
  }, [connect, disconnect]);

  return (
    <div className="flex h-full bg-canvas text-ink" data-testid="workspace-root">
      <aside
        className="hidden w-60 shrink-0 flex-col border-r border-line bg-canvas-sunken px-3 py-5 md:flex"
        aria-label="Workspace"
      >
        <Link
          to="/"
          className="mx-2 mb-6 flex items-center gap-2 text-ink"
          aria-label="Sir Thaddeus home"
        >
          <span className="flex h-8 w-8 items-center justify-center rounded-xl bg-accent text-white">
            <Sparkles className="h-4 w-4" strokeWidth={2} />
          </span>
          <span className="text-[15px] font-semibold tracking-tightest">Sir Thaddeus</span>
        </Link>

        <NavGroup items={primaryNav} />
        <div className="my-4 h-px bg-line" />
        <NavGroup items={secondaryNav} />

        <div className="mt-auto px-2 pt-4 text-[11px] text-ink-subtle">
          <div className="flex items-center justify-between">
            <span data-testid="runtime-version">v{meta.version}</span>
            <span className="font-mono lowercase tracking-wide">local</span>
          </div>
        </div>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-12 items-center justify-between border-b border-line bg-canvas/80 px-4 backdrop-blur md:px-6">
          <nav className="flex items-center gap-1 md:hidden" aria-label="Primary">
            {primaryNav.concat(secondaryNav).map(({ to, label }) => (
              <Link
                key={to}
                to={to}
                activeProps={{ className: 'bg-accent-soft text-ink' }}
                className="rounded-full px-2.5 py-1 text-xs text-ink-muted hover:bg-accent-soft hover:text-ink"
              >
                {label}
              </Link>
            ))}
          </nav>
          <div className="hidden md:block" />
          <div className="flex items-center gap-3 text-xs text-ink-muted">
            <RuntimeStateBadge />
          </div>
        </header>

        <main className="flex-1 overflow-y-auto">
          <Outlet />
        </main>
      </div>
    </div>
  );
}

function NavGroup({ items }: { items: ReadonlyArray<NavEntry> }) {
  return (
    <ul className="space-y-0.5">
      {items.map(({ to, label, icon: Icon }) => (
        <li key={to}>
          <Link
            to={to}
            activeProps={{
              className: 'bg-accent-soft text-ink',
            }}
            activeOptions={{ exact: to === '/' }}
            className="flex items-center gap-2.5 rounded-xl px-2.5 py-1.5 text-sm text-ink-muted transition hover:bg-accent-soft hover:text-ink"
          >
            <Icon className="h-[18px] w-[18px]" strokeWidth={1.75} />
            <span>{label}</span>
          </Link>
        </li>
      ))}
    </ul>
  );
}
