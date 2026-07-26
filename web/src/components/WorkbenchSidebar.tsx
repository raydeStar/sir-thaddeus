import { useEffect, useState } from 'react';
import { Link } from '@tanstack/react-router';
import {
  Activity,
  BookOpen,
  ChevronDown,
  ClipboardList,
  Cog,
  Database,
  Gauge,
  History,
  Library,
  MessageSquarePlus,
  Search,
} from 'lucide-react';
import { listThreads } from '../lib/chatApi';
import { listWikiRoots, type WikiRoot } from '../lib/wikiApi';
import type { ThreadSummary } from '@thaddeus/shared-types';
import { useCommandPaletteStore } from '../stores/commandPaletteStore';
import { ThaddeusSignet } from './ThaddeusSignet';

export function WorkbenchSidebar({ versionLabel }: { versionLabel: string }) {
  const showPalette = useCommandPaletteStore((state) => state.show);
  const [threads, setThreads] = useState<ThreadSummary[]>([]);
  const [roots, setRoots] = useState<WikiRoot[]>([]);

  useEffect(() => {
    let disposed = false;
    void Promise.all([listThreads(), listWikiRoots()])
      .then(([nextThreads, nextRoots]) => {
        if (disposed) return;
        setThreads(nextThreads.slice(0, 5));
        setRoots(nextRoots.slice(0, 5));
      })
      .catch(() => {
        // Runtime-backed lists are helpful navigation, not a shell blocker.
      });
    return () => {
      disposed = true;
    };
  }, []);

  return (
    <aside
      className="shell-sidebar hidden w-[248px] shrink-0 flex-col border-r border-line md:flex"
      aria-label="Workspace"
      data-testid="desktop-sidebar"
    >
      <Link to="/" className="flex h-14 items-center gap-3 border-b border-line px-4" aria-label="Sir Thaddeus home">
        <ThaddeusSignet className="h-8 w-8 shrink-0" />
        <span className="min-w-0">
          <strong className="block truncate text-sm font-semibold text-ink">Sir Thaddeus</strong>
          <span className="mt-0.5 block text-[9px] font-semibold uppercase tracking-[0.14em] text-ink-subtle">
            Local workbench
          </span>
        </span>
      </Link>

      <div className="grid gap-2 p-3">
        <Link
          to="/"
          className="flex min-h-10 items-center gap-2.5 rounded-xl border border-accent/25 bg-accent-soft px-3 text-sm font-medium text-ink transition hover:border-accent/45"
          data-testid="sidebar-new-conversation"
        >
          <MessageSquarePlus className="h-4 w-4 text-accent" />
          New conversation
          <kbd className="ml-auto text-[9px] text-ink-subtle">Ctrl N</kbd>
        </Link>
        <button
          type="button"
          onClick={showPalette}
          className="flex min-h-10 items-center gap-2.5 rounded-xl border border-line bg-canvas-raised px-3 text-left text-sm text-ink-muted transition hover:border-line-strong hover:text-ink"
          data-testid="sidebar-search-everything"
        >
          <Search className="h-4 w-4" />
          Search everything
          <kbd className="ml-auto text-[9px] text-ink-subtle">Ctrl Space</kbd>
        </button>
      </div>

      <nav className="min-h-0 flex-1 overflow-y-auto px-2 pb-4" aria-label="Primary">
        <SidebarLabel label="Workspaces" count={roots.length || undefined} />
        {roots.length > 0 ? roots.map((root) => (
          <a
            key={root.id}
            href={`/wiki?rootId=${encodeURIComponent(root.id)}`}
            className="sidebar-row"
          >
            <span className="h-2 w-2 shrink-0 rounded-[3px] bg-accent shadow-[0_0_0_4px_var(--color-accent-soft)]" />
            <span className="truncate">{root.name}</span>
          </a>
        )) : (
          <Link to="/wiki" className="sidebar-row">
            <Library className="h-4 w-4" />
            Wiki workspaces
          </Link>
        )}

        <SidebarLabel label="Recent" />
        {threads.length > 0 ? threads.map((thread) => (
          <Link
            key={thread.id}
            to="/chat/$threadId"
            params={{ threadId: thread.id }}
            className="sidebar-row pl-7"
            activeProps={{ className: 'bg-canvas-raised text-ink ring-1 ring-inset ring-line' }}
          >
            <span className="min-w-0 flex-1 truncate">{thread.title || 'Untitled conversation'}</span>
            <span className="text-[9px] text-ink-subtle">{shortRelative(thread.updatedAt)}</span>
          </Link>
        )) : (
          <Link to="/chat" className="sidebar-row pl-7">
            <History className="h-3.5 w-3.5" />
            Conversation history
          </Link>
        )}

        <SidebarLabel label="Knowledge" />
        <Link to="/wiki" className="sidebar-row" activeProps={{ className: 'bg-canvas-raised text-ink ring-1 ring-inset ring-line' }}>
          <BookOpen className="h-4 w-4" />
          Wiki and files
        </Link>
        <Link to="/routines" className="sidebar-row" activeProps={{ className: 'bg-canvas-raised text-ink ring-1 ring-inset ring-line' }}>
          <ClipboardList className="h-4 w-4" />
          Routines
        </Link>

        <details className="group/system mt-3">
          <summary className="sidebar-row cursor-pointer list-none">
            <Cog className="h-4 w-4" />
            <span className="flex-1">System</span>
            <ChevronDown className="h-3.5 w-3.5 transition-transform group-open/system:rotate-180" />
          </summary>
          <div className="mt-1 space-y-0.5 pl-4">
            <SystemLink to="/activity" label="Activity and audit" icon={Activity} />
            <SystemLink to="/memory" label="Memory" icon={BookOpen} />
            <SystemLink to="/modules" label="Data" icon={Database} />
            <SystemLink to="/settings" label="Settings" icon={Cog} />
            <SystemLink to="/diagnostics" label="Diagnostics" icon={Gauge} />
          </div>
        </details>
      </nav>

      <div className="border-t border-line px-4 py-3 text-[10px] text-ink-subtle">
        <span data-testid="runtime-version">{versionLabel}</span>
        <span className="mx-2 opacity-60">-</span>
        <span className="font-mono lowercase tracking-wide">local</span>
      </div>
    </aside>
  );
}

function SidebarLabel({ label, count }: { label: string; count?: number }) {
  return (
    <div className="flex items-center justify-between px-2 pb-1 pt-4 text-[9px] font-bold uppercase tracking-[0.14em] text-ink-subtle">
      <span>{label}</span>
      {count != null ? <span>{count}</span> : null}
    </div>
  );
}

function SystemLink({ to, label, icon: Icon }: { to: string; label: string; icon: typeof Cog }) {
  return (
    <Link
      to={to}
      className="sidebar-row"
      activeProps={{ className: 'bg-canvas-raised text-ink ring-1 ring-inset ring-line' }}
    >
      <Icon className="h-3.5 w-3.5" />
      {label}
    </Link>
  );
}

function shortRelative(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  const hours = Math.max(0, Math.round((Date.now() - date.getTime()) / 3_600_000));
  if (hours < 1) return 'now';
  if (hours < 24) return `${hours}h`;
  return `${Math.round(hours / 24)}d`;
}
