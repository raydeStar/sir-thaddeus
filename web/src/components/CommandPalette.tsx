import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from '@tanstack/react-router';
import {
  Activity,
  BookOpen,
  ChevronRight,
  ClipboardList,
  FileText,
  MessageSquareText,
  Plus,
  Search,
  Settings,
  Sparkles,
  X,
  type LucideIcon,
} from 'lucide-react';
import { listThreads } from '../lib/chatApi';
import { listTurnTraces } from '../lib/activityApi';
import { searchWiki } from '../lib/wikiApi';
import { useCommandPaletteStore } from '../stores/commandPaletteStore';
import { useWorkbenchStore } from '../stores/workbenchStore';

type PaletteMode = 'all' | 'commands' | 'people' | 'wiki' | 'actions';
type ResultKind = 'command' | 'conversation' | 'wiki' | 'output';

interface PaletteResult {
  id: string;
  kind: ResultKind;
  title: string;
  detail: string;
  badge?: string;
  icon: LucideIcon;
  run: () => void;
  secondary?: () => void;
}

const COMMANDS: Array<{
  id: string;
  title: string;
  detail: string;
  to: string;
  icon: LucideIcon;
}> = [
  { id: 'new-chat', title: 'New conversation', detail: 'Start with a clean composer', to: '/', icon: Plus },
  { id: 'conversations', title: 'Open conversations', detail: 'Search and resume recent work', to: '/chat', icon: MessageSquareText },
  { id: 'wiki', title: 'Open Wiki workspaces', detail: 'Durable local pages and knowledge', to: '/wiki', icon: BookOpen },
  { id: 'routines', title: 'Open routines', detail: 'Repeatable local workflows', to: '/routines', icon: ClipboardList },
  { id: 'audit', title: 'Open audit log', detail: 'Inspect turn traces and runtime evidence', to: '/settings?tab=logs', icon: Activity },
  { id: 'settings', title: 'Open system settings', detail: 'Permissions, voice, models, and files', to: '/settings', icon: Settings },
];

export function CommandPalette() {
  const open = useCommandPaletteStore((state) => state.open);
  const show = useCommandPaletteStore((state) => state.show);
  const hide = useCommandPaletteStore((state) => state.hide);
  const openWikiPage = useWorkbenchStore((state) => state.openWikiPage);
  const navigate = useNavigate();
  const inputRef = useRef<HTMLInputElement>(null);
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState(0);
  const [remoteResults, setRemoteResults] = useState<PaletteResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [actionsOpen, setActionsOpen] = useState(false);

  useEffect(() => {
    const handler = (event: KeyboardEvent) => {
      const isSpace = event.code === 'Space' || event.key === ' ' || event.key === 'Spacebar';
      if (!isSpace || !event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
      event.preventDefault();
      show();
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [show]);

  useEffect(() => {
    if (!open) return;
    setSelected(0);
    setActionsOpen(false);
    const timeout = window.setTimeout(() => inputRef.current?.focus(), 0);
    return () => window.clearTimeout(timeout);
  }, [open]);

  const parsed = useMemo(() => parseQuery(query), [query]);

  useEffect(() => {
    if (!open) return;
    let disposed = false;
    const timeout = window.setTimeout(() => {
      setLoading(true);
      void Promise.all([
        (parsed.mode === 'wiki' || parsed.mode === 'all') && parsed.text
          ? searchWiki(null, parsed.text)
          : Promise.resolve([]),
        parsed.mode === 'wiki' || parsed.mode === 'people' || parsed.mode === 'actions'
          ? Promise.resolve([])
          : listThreads(),
        parsed.mode === 'all' ? listTurnTraces(12) : Promise.resolve([]),
      ])
        .then(([wiki, threads, traces]) => {
          if (disposed) return;
          const normalized = parsed.text.toLowerCase();
          const next: PaletteResult[] = [];

          for (const thread of threads) {
            if (normalized && !`${thread.title} ${thread.lastMessagePreview ?? ''}`.toLowerCase().includes(normalized)) continue;
            next.push({
              id: `thread-${thread.id}`,
              kind: 'conversation',
              title: thread.title || 'Untitled conversation',
              detail: thread.lastMessagePreview || `${thread.messageCount} messages`,
              badge: formatRelative(thread.updatedAt),
              icon: MessageSquareText,
              run: () => {
                hide();
                void navigate({ to: '/chat/$threadId', params: { threadId: thread.id } });
              },
            });
          }

          for (const result of wiki) {
            next.push({
              id: `wiki-${result.pageId}`,
              kind: 'wiki',
              title: result.title,
              detail: result.excerpt || result.relativePath,
              badge: 'Local',
              icon: BookOpen,
              run: () => {
                hide();
                openWikiPage(result.pageId);
              },
              secondary: () => {
                hide();
                window.location.assign(`/wiki?pageId=${encodeURIComponent(result.pageId)}`);
              },
            });
          }

          for (const trace of traces) {
            if (!trace.threadId) continue;
            next.push({
              id: `trace-${trace.messageId}`,
              kind: 'output',
              title: `Work receipt - ${formatRelative(trace.modifiedAt)}`,
              detail: `${trace.eventCount} verified runtime events`,
              badge: 'Local',
              icon: FileText,
              run: () => {
                hide();
                void navigate({
                  to: '/chat/$threadId',
                  params: { threadId: trace.threadId! },
                  search: { focusMessageId: trace.messageId },
                });
              },
            });
          }
          setRemoteResults(next.slice(0, 18));
        })
        .catch(() => {
          if (!disposed) setRemoteResults([]);
        })
        .finally(() => {
          if (!disposed) setLoading(false);
        });
    }, parsed.text ? 140 : 0);

    return () => {
      disposed = true;
      window.clearTimeout(timeout);
    };
  }, [hide, navigate, open, openWikiPage, parsed.mode, parsed.text]);

  const commandResults = useMemo<PaletteResult[]>(() => {
    if (parsed.mode !== 'commands' && parsed.mode !== 'all' && parsed.mode !== 'actions') return [];
    const needle = parsed.text.toLowerCase();
    return COMMANDS
      .filter((command) => !needle || `${command.title} ${command.detail}`.toLowerCase().includes(needle))
      .map((command) => ({
        ...command,
        kind: 'command' as const,
        run: () => {
          hide();
          window.location.assign(command.to);
        },
      }));
  }, [hide, parsed.mode, parsed.text]);

  const results = parsed.mode === 'people'
    ? []
    : [...commandResults, ...remoteResults];
  const safeSelected = Math.min(selected, Math.max(0, results.length - 1));

  useEffect(() => {
    setSelected(0);
  }, [parsed.mode, parsed.text]);

  if (!open) return null;

  function choose(index = safeSelected, secondary = false) {
    const result = results[index];
    if (!result) return;
    if (secondary && result.secondary) result.secondary();
    else result.run();
  }

  return (
    <div
      className="fixed inset-0 z-[70] flex items-start justify-center bg-canvas/70 px-3 pt-[10vh] backdrop-blur-sm"
      role="presentation"
      onMouseDown={(event) => {
        if (event.currentTarget === event.target) hide();
      }}
    >
      <section
        role="dialog"
        aria-modal="true"
        aria-label="Search everything"
        className="command-palette"
        data-testid="command-palette"
      >
        <div className="flex items-center gap-3 border-b border-line px-4">
          <Search className="h-4 w-4 shrink-0 text-ink-subtle" aria-hidden />
          <input
            ref={inputRef}
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Escape') {
                event.preventDefault();
                hide();
              } else if (event.key === 'ArrowDown' || (event.ctrlKey && event.key.toLowerCase() === 'n')) {
                event.preventDefault();
                setSelected((value) => Math.min(results.length - 1, value + 1));
              } else if (event.key === 'ArrowUp' || (event.ctrlKey && event.key.toLowerCase() === 'p')) {
                event.preventDefault();
                setSelected((value) => Math.max(0, value - 1));
              } else if (event.key === 'Enter') {
                event.preventDefault();
                choose();
              } else if (event.ctrlKey && event.key.toLowerCase() === 'k') {
                event.preventDefault();
                setActionsOpen((value) => !value);
              }
            }}
            placeholder="Search conversations, Wiki, outputs, or type > for commands"
            className="h-14 min-w-0 flex-1 bg-transparent text-[15px] text-ink outline-none placeholder:text-ink-subtle"
            aria-controls="command-palette-results"
            aria-activedescendant={results[safeSelected] ? `palette-result-${results[safeSelected].id}` : undefined}
          />
          {loading ? <span className="agent-breathing-dot" aria-label="Searching" /> : null}
          <button type="button" className="wiki-icon-button h-8 w-8" onClick={hide} aria-label="Close search">
            <X className="h-4 w-4" />
          </button>
        </div>

        <div className="flex flex-wrap gap-1.5 border-b border-line px-4 py-2 text-[10px] text-ink-subtle" aria-label="Search prefixes">
          <PrefixHint prefix=">" label="commands" active={parsed.mode === 'commands'} />
          <PrefixHint prefix="#" label="Wiki" active={parsed.mode === 'wiki'} />
          <PrefixHint prefix="@" label="people" active={parsed.mode === 'people'} />
          <PrefixHint prefix="/" label="current object" active={parsed.mode === 'actions'} />
        </div>

        <div id="command-palette-results" role="listbox" className="max-h-[52vh] overflow-y-auto p-2">
          {results.length > 0 ? results.map((result, index) => {
            const Icon = result.icon;
            const active = index === safeSelected;
            return (
              <button
                key={result.id}
                id={`palette-result-${result.id}`}
                type="button"
                role="option"
                aria-selected={active}
                onMouseEnter={() => setSelected(index)}
                onClick={() => choose(index)}
                className={`flex min-h-14 w-full items-center gap-3 rounded-xl px-3 py-2 text-left transition ${
                  active ? 'bg-accent-soft text-ink' : 'text-ink-muted hover:bg-canvas-sunken hover:text-ink'
                }`}
              >
                <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-line bg-canvas-raised">
                  <Icon className="h-4 w-4" strokeWidth={1.8} />
                </span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-sm font-medium text-ink">{result.title}</span>
                  <span className="mt-0.5 block truncate text-[11px] text-ink-muted">{result.detail}</span>
                </span>
                {result.badge ? <span className="rounded-full border border-line px-2 py-0.5 text-[9px] text-ink-subtle">{result.badge}</span> : null}
                <span className="text-[9px] font-semibold uppercase tracking-[0.08em] text-ink-subtle">{result.kind}</span>
                <ChevronRight className="h-3.5 w-3.5 text-ink-subtle" />
              </button>
            );
          }) : (
            <div className="px-4 py-10 text-center">
              <Sparkles className="mx-auto h-5 w-5 text-accent" />
              <p className="mt-3 text-sm font-medium text-ink">
                {parsed.mode === 'people' ? 'People profiles are not configured yet.' : 'Nothing matched that search.'}
              </p>
              <p className="mt-1 text-xs text-ink-muted">
                Try "summarize the active window", "# release", or "&gt; audit log".
              </p>
            </div>
          )}
        </div>

        {actionsOpen && results[safeSelected] ? (
          <div className="border-t border-line bg-canvas-sunken px-4 py-3">
            <p className="text-[10px] font-semibold uppercase tracking-[0.1em] text-ink-subtle">Actions</p>
            <div className="mt-2 flex flex-wrap gap-2">
              <button type="button" className="btn-primary min-h-9 text-xs" onClick={() => choose()}>
                Open
              </button>
              {results[safeSelected].secondary ? (
                <button type="button" className="btn-quiet min-h-9 text-xs" onClick={() => choose(safeSelected, true)}>
                  Open in full Wiki
                </button>
              ) : null}
            </div>
          </div>
        ) : null}

        <footer className="flex items-center gap-3 border-t border-line px-4 py-2 text-[10px] text-ink-subtle">
          <span><kbd>↑↓</kbd> move</span>
          <span><kbd>Enter</kbd> open</span>
          <span><kbd>Ctrl K</kbd> actions</span>
          <span className="ml-auto"><kbd>Esc</kbd> close</span>
        </footer>
      </section>
    </div>
  );
}

function PrefixHint({ prefix, label, active }: { prefix: string; label: string; active: boolean }) {
  return (
    <span className={`rounded-md px-2 py-1 ${active ? 'bg-accent-soft text-accent' : 'bg-canvas-sunken'}`}>
      <strong className="mr-1 font-mono text-ink">{prefix}</strong>{label}
    </span>
  );
}

function parseQuery(value: string): { mode: PaletteMode; text: string } {
  const trimmedStart = value.trimStart();
  const prefix = trimmedStart[0];
  if (prefix === '>') return { mode: 'commands', text: trimmedStart.slice(1).trim() };
  if (prefix === '#') return { mode: 'wiki', text: trimmedStart.slice(1).trim() };
  if (prefix === '@') return { mode: 'people', text: trimmedStart.slice(1).trim() };
  if (prefix === '/') return { mode: 'actions', text: trimmedStart.slice(1).trim() };
  return { mode: 'all', text: value.trim() };
}

function formatRelative(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  const seconds = Math.round((date.getTime() - Date.now()) / 1000);
  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
  if (Math.abs(seconds) < 60) return formatter.format(seconds, 'second');
  const minutes = Math.round(seconds / 60);
  if (Math.abs(minutes) < 60) return formatter.format(minutes, 'minute');
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 24) return formatter.format(hours, 'hour');
  return formatter.format(Math.round(hours / 24), 'day');
}
