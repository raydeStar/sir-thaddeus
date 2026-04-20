import { createRootRoute, Link, Outlet } from '@tanstack/react-router';
import { useEffect } from 'react';
import { useRuntimeStore } from '../stores/runtimeStore';
import { RuntimeStateBadge } from '../components/RuntimeStateBadge';
import { readRuntimeMetadata } from '../lib/runtime';

export const Route = createRootRoute({
  component: RootLayout,
});

const navItems: ReadonlyArray<[string, string]> = [
  ['/', 'Home'],
  ['/chat', 'Chat'],
  ['/history', 'History'],
  ['/activity', 'Activity'],
  ['/memory', 'Memory'],
  ['/automations', 'Automations'],
  ['/settings', 'Settings'],
  ['/diagnostics', 'Diagnostics'],
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
    <div className="flex h-full flex-col" data-testid="workspace-root">
      <header className="flex items-center justify-between border-b border-slate-200 bg-white px-4 py-2">
        <nav className="flex items-center gap-1" aria-label="Primary">
          {navItems.map(([to, label]) => (
            <Link
              key={to}
              to={to}
              activeProps={{ className: 'bg-thaddeus-mist text-thaddeus-ink' }}
              className="rounded px-3 py-1 text-sm text-slate-600 hover:bg-slate-100"
            >
              {label}
            </Link>
          ))}
        </nav>
        <div className="flex items-center gap-3 text-xs text-slate-500">
          <span data-testid="runtime-version">v{meta.version}</span>
          <RuntimeStateBadge />
        </div>
      </header>
      <main className="flex-1 overflow-y-auto">
        <Outlet />
      </main>
    </div>
  );
}
