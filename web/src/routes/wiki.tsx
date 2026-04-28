import { createFileRoute } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import {
  BookOpenText,
  ChevronDown,
  Circle,
  Clock3,
  FileText,
  Folder,
  Library,
  PanelLeftClose,
  PanelLeftOpen,
  PanelRightClose,
  PanelRightOpen,
  Plus,
  Save,
  Search,
  Settings2,
  Sparkles,
  Undo2,
} from 'lucide-react';

export const Route = createFileRoute('/wiki')({
  component: WikiRoute,
});

type WikiScope = 'root' | 'folder' | 'page';

interface WikiPagePreview {
  id: string;
  title: string;
  folderId: string;
  updatedAt: string;
  excerpt: string;
  status: 'saved' | 'draft' | 'ai-edited';
  wordCount: number;
}

interface WikiFolderPreview {
  id: string;
  title: string;
  pageIds: string[];
}

interface WikiRootPreview {
  id: string;
  title: string;
  path: string;
  folders: WikiFolderPreview[];
  pages: WikiPagePreview[];
}

const roots: WikiRootPreview[] = [
  {
    id: 'root_personal',
    title: 'Personal Wiki',
    path: '~/Documents/Sir Thaddeus Wiki/Personal',
    folders: [
      { id: 'folder_home', title: 'Home Operations', pageIds: ['page_command_center', 'page_house_notes'] },
      { id: 'folder_projects', title: 'Projects', pageIds: ['page_canvas_spec'] },
    ],
    pages: [
      {
        id: 'page_command_center',
        title: 'Command Center',
        folderId: 'folder_home',
        updatedAt: 'Today 10:42 AM',
        excerpt: 'Operating notes, important links, and running decisions for the local assistant workspace.',
        status: 'saved',
        wordCount: 684,
      },
      {
        id: 'page_house_notes',
        title: 'House Notes',
        folderId: 'folder_home',
        updatedAt: 'Yesterday 8:12 PM',
        excerpt: 'Reusable reminders and maintenance context that should stay local.',
        status: 'draft',
        wordCount: 312,
      },
      {
        id: 'page_canvas_spec',
        title: 'Wiki Canvas Design Spec',
        folderId: 'folder_projects',
        updatedAt: 'Apr 27 2:04 PM',
        excerpt: 'Canvas, page chat, scoped retrieval, revision safety, and local-first storage boundaries.',
        status: 'ai-edited',
        wordCount: 1288,
      },
    ],
  },
  {
    id: 'root_workshop',
    title: 'Workshop',
    path: '~/Documents/Sir Thaddeus Wiki/Workshop',
    folders: [
      { id: 'folder_research', title: 'Research', pageIds: ['page_retrieval'] },
    ],
    pages: [
      {
        id: 'page_retrieval',
        title: 'Retrieval Notes',
        folderId: 'folder_research',
        updatedAt: 'Apr 26 4:31 PM',
        excerpt: 'FTS baseline, root selectors, and permission gates for read/write wiki tools.',
        status: 'saved',
        wordCount: 956,
      },
    ],
  },
];

const initialMarkdown = `# Command Center

## Current Shape

Sir Thaddeus should treat wiki pages as user-owned local files. Markdown remains the canonical body, while SQLite stores metadata, tree shape, search indexes, and revision history that can be rebuilt.

## Guardrails

- Keep roots explicit.
- Never retrieve wiki context in normal chat unless the user selected a scope.
- Save AI edits through the same versioned page API as manual edits.
`;

function WikiRoute() {
  const [selectedRootId, setSelectedRootId] = useState(roots[0].id);
  const [selectedFolderId, setSelectedFolderId] = useState(roots[0].folders[0].id);
  const [selectedPageId, setSelectedPageId] = useState(roots[0].pages[0].id);
  const [search, setSearch] = useState('');
  const [scope, setScope] = useState<WikiScope>('page');
  const [leftCollapsed, setLeftCollapsed] = useState(false);
  const [rightCollapsed, setRightCollapsed] = useState(false);
  const [markdown, setMarkdown] = useState(initialMarkdown);
  const [dirty, setDirty] = useState(false);

  const selectedRoot = roots.find((root) => root.id === selectedRootId) ?? roots[0];
  const selectedPage = selectedRoot.pages.find((page) => page.id === selectedPageId) ?? selectedRoot.pages[0];
  const selectedFolder = selectedRoot.folders.find((folder) => folder.id === selectedFolderId) ?? selectedRoot.folders[0];
  const filteredPages = useMemo(() => {
    const query = search.trim().toLowerCase();
    if (!query) return selectedRoot.pages;
    return selectedRoot.pages.filter((page) => {
      return page.title.toLowerCase().includes(query) || page.excerpt.toLowerCase().includes(query);
    });
  }, [search, selectedRoot.pages]);

  const onRootChange = (rootId: string) => {
    const root = roots.find((candidate) => candidate.id === rootId) ?? roots[0];
    const firstFolder = root.folders[0];
    const firstPage = root.pages[0];
    setSelectedRootId(root.id);
    setSelectedFolderId(firstFolder?.id ?? '');
    setSelectedPageId(firstPage?.id ?? '');
    setScope('root');
  };

  const onPageSelect = (page: WikiPagePreview) => {
    setSelectedPageId(page.id);
    setSelectedFolderId(page.folderId);
    setScope('page');
  };

  const onMarkdownChange = (value: string) => {
    setMarkdown(value);
    setDirty(true);
  };

  return (
    <section className="flex min-h-[calc(100vh-2.75rem)] flex-col bg-canvas" data-testid="route-wiki">
      <header className="flex min-h-[72px] flex-col gap-3 border-b border-line px-4 py-3 md:flex-row md:items-center md:justify-between md:px-6">
        <div className="min-w-0">
          <div className="flex items-center gap-2 text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
            <Library className="h-3.5 w-3.5" strokeWidth={1.8} />
            Wiki Canvas
          </div>
          <div className="mt-1 flex min-w-0 flex-wrap items-center gap-2">
            <h1 className="truncate text-xl font-semibold text-ink">{selectedPage?.title ?? selectedRoot.title}</h1>
            <ScopeChip scope={scope} root={selectedRoot.title} folder={selectedFolder?.title} page={selectedPage?.title} />
            <span className="inline-flex items-center gap-1 rounded-full border border-line px-2 py-0.5 text-[11px] text-ink-muted">
              <Circle className={`h-2 w-2 ${dirty ? 'fill-amber-500 text-amber-500' : 'fill-emerald-500 text-emerald-500'}`} />
              {dirty ? 'Unsaved' : 'Saved'}
            </span>
          </div>
        </div>

        <div className="flex shrink-0 flex-wrap items-center gap-2">
          <button type="button" className="wiki-icon-button" title="New root" aria-label="New root">
            <Library className="h-4 w-4" strokeWidth={1.8} />
          </button>
          <button type="button" className="wiki-icon-button" title="New folder" aria-label="New folder">
            <Folder className="h-4 w-4" strokeWidth={1.8} />
          </button>
          <button type="button" className="wiki-command-button">
            <Plus className="h-4 w-4" strokeWidth={1.9} />
            New page
          </button>
          <button type="button" className="wiki-command-button" disabled={!dirty} onClick={() => setDirty(false)}>
            <Save className="h-4 w-4" strokeWidth={1.9} />
            Save
          </button>
        </div>
      </header>

      <div
        className="grid min-h-0 flex-1 grid-cols-1 md:grid-cols-[var(--wiki-left)_minmax(0,1fr)_var(--wiki-right)]"
        style={{
          '--wiki-left': leftCollapsed ? '56px' : '304px',
          '--wiki-right': rightCollapsed ? '56px' : '336px',
        } as React.CSSProperties}
      >
        <aside className="min-h-0 border-b border-line bg-canvas md:border-b-0 md:border-r" aria-label="Wiki tree">
          <PanelHeader
            title="Roots"
            collapsed={leftCollapsed}
            onToggle={() => setLeftCollapsed((value) => !value)}
            collapsedIcon={<PanelLeftOpen className="h-4 w-4" strokeWidth={1.8} />}
            expandedIcon={<PanelLeftClose className="h-4 w-4" strokeWidth={1.8} />}
          />
          {!leftCollapsed ? (
            <div className="space-y-4 px-4 pb-4">
              <label className="block text-xs font-medium text-ink-muted" htmlFor="wiki-root-select">
                Root
              </label>
              <div className="relative">
                <select
                  id="wiki-root-select"
                  value={selectedRootId}
                  onChange={(event) => onRootChange(event.target.value)}
                  className="w-full appearance-none rounded-xl border border-line bg-canvas-raised px-3 py-2 pr-8 text-sm text-ink outline-none transition focus:border-accent focus:ring-2 focus:ring-accent/15"
                >
                  {roots.map((root) => (
                    <option key={root.id} value={root.id}>{root.title}</option>
                  ))}
                </select>
                <ChevronDown className="pointer-events-none absolute right-2.5 top-2.5 h-4 w-4 text-ink-subtle" strokeWidth={1.8} />
              </div>
              <p className="truncate text-[11px] text-ink-subtle">{selectedRoot.path}</p>

              <div className="relative">
                <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-ink-subtle" strokeWidth={1.8} />
                <input
                  type="search"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  placeholder="Search pages"
                  className="w-full rounded-xl border border-line bg-canvas-raised py-2 pl-9 pr-3 text-sm text-ink outline-none transition placeholder:text-ink-subtle focus:border-accent focus:ring-2 focus:ring-accent/15"
                />
              </div>

              <nav className="space-y-3" aria-label="Wiki folders">
                {selectedRoot.folders.map((folder) => (
                  <section key={folder.id}>
                    <button
                      type="button"
                      onClick={() => {
                        setSelectedFolderId(folder.id);
                        setScope('folder');
                      }}
                      className={`flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-xs font-medium uppercase tracking-[0.08em] transition ${selectedFolderId === folder.id && scope === 'folder' ? 'bg-accent-soft text-ink' : 'text-ink-subtle hover:text-ink'}`}
                    >
                      <Folder className="h-3.5 w-3.5" strokeWidth={1.8} />
                      <span className="truncate">{folder.title}</span>
                    </button>
                    <ul className="mt-1 space-y-1">
                      {filteredPages.filter((page) => page.folderId === folder.id).map((page) => (
                        <li key={page.id}>
                          <button
                            type="button"
                            onClick={() => onPageSelect(page)}
                            className={`flex w-full items-start gap-2 rounded-xl px-2 py-2 text-left transition ${selectedPageId === page.id ? 'bg-canvas-raised text-ink shadow-soft' : 'text-ink-muted hover:bg-canvas-raised/70 hover:text-ink'}`}
                          >
                            <FileText className="mt-0.5 h-4 w-4 shrink-0" strokeWidth={1.8} />
                            <span className="min-w-0">
                              <span className="block truncate text-sm font-medium">{page.title}</span>
                              <span className="mt-0.5 block truncate text-[11px] text-ink-subtle">{page.updatedAt}</span>
                            </span>
                          </button>
                        </li>
                      ))}
                    </ul>
                  </section>
                ))}
              </nav>
            </div>
          ) : null}
        </aside>

        <main className="min-h-0 overflow-hidden">
          <div className="flex h-full min-h-[640px] flex-col">
            <div className="flex items-center justify-between border-b border-line px-4 py-2 md:px-5">
              <div className="flex min-w-0 items-center gap-2 text-xs text-ink-muted">
                <BookOpenText className="h-4 w-4 shrink-0" strokeWidth={1.8} />
                <span className="truncate">{selectedRoot.title} / {selectedFolder?.title ?? 'Unfiled'}</span>
              </div>
              <div className="flex items-center gap-1.5">
                <button type="button" className="wiki-icon-button" title="Page settings" aria-label="Page settings">
                  <Settings2 className="h-4 w-4" strokeWidth={1.8} />
                </button>
                <button type="button" className="wiki-icon-button" title="Undo AI edit" aria-label="Undo AI edit">
                  <Undo2 className="h-4 w-4" strokeWidth={1.8} />
                </button>
              </div>
            </div>

            <textarea
              value={markdown}
              onChange={(event) => onMarkdownChange(event.target.value)}
              spellCheck
              className="min-h-0 flex-1 resize-none bg-canvas px-5 py-5 font-mono text-sm leading-6 text-ink outline-none placeholder:text-ink-subtle md:px-8"
              aria-label="Wiki markdown canvas"
            />

            <footer className="flex flex-wrap items-center justify-between gap-2 border-t border-line px-4 py-2 text-[11px] text-ink-subtle md:px-5">
              <span>{selectedPage?.wordCount ?? 0} words</span>
              <span>{selectedPage?.status === 'ai-edited' ? 'Latest revision includes AI edit' : 'Manual revision head'}</span>
            </footer>
          </div>
        </main>

        <aside className="min-h-0 border-t border-line bg-canvas md:border-l md:border-t-0" aria-label="Page chat and revisions">
          <PanelHeader
            title="Page"
            collapsed={rightCollapsed}
            onToggle={() => setRightCollapsed((value) => !value)}
            collapsedIcon={<PanelRightOpen className="h-4 w-4" strokeWidth={1.8} />}
            expandedIcon={<PanelRightClose className="h-4 w-4" strokeWidth={1.8} />}
          />
          {!rightCollapsed ? (
            <div className="space-y-5 px-4 pb-4">
              <section className="space-y-2">
                <h2 className="text-xs font-semibold uppercase tracking-[0.08em] text-ink-subtle">Context</h2>
                <div className="flex flex-wrap gap-2">
                  {(['root', 'folder', 'page'] as WikiScope[]).map((candidate) => (
                    <button
                      key={candidate}
                      type="button"
                      onClick={() => setScope(candidate)}
                      className={`rounded-full border px-3 py-1 text-xs capitalize transition ${scope === candidate ? 'border-accent bg-accent-soft text-ink' : 'border-line text-ink-muted hover:text-ink'}`}
                    >
                      {candidate}
                    </button>
                  ))}
                </div>
              </section>

              <section className="space-y-2">
                <h2 className="text-xs font-semibold uppercase tracking-[0.08em] text-ink-subtle">Page Chat</h2>
                <div className="rounded-xl border border-line bg-canvas-raised p-3">
                  <p className="text-sm text-ink-muted">Draft against the selected scope.</p>
                  <div className="mt-3 flex items-center gap-2 rounded-xl border border-line bg-canvas px-3 py-2 text-sm text-ink-subtle">
                    <Sparkles className="h-4 w-4 shrink-0" strokeWidth={1.8} />
                    Ask about this page
                  </div>
                </div>
              </section>

              <section className="space-y-2">
                <h2 className="text-xs font-semibold uppercase tracking-[0.08em] text-ink-subtle">Revisions</h2>
                <ol className="space-y-2">
                  <RevisionItem label="Current head" detail={selectedPage?.updatedAt ?? 'Now'} active />
                  <RevisionItem label="AI edit" detail="Apr 27 1:58 PM" />
                  <RevisionItem label="Manual save" detail="Apr 27 12:16 PM" />
                </ol>
              </section>
            </div>
          ) : null}
        </aside>
      </div>
    </section>
  );
}

function PanelHeader({
  title,
  collapsed,
  onToggle,
  collapsedIcon,
  expandedIcon,
}: {
  title: string;
  collapsed: boolean;
  onToggle: () => void;
  collapsedIcon: React.ReactNode;
  expandedIcon: React.ReactNode;
}) {
  return (
    <div className="flex h-12 items-center justify-between px-3">
      {!collapsed ? <h2 className="text-xs font-semibold uppercase tracking-[0.08em] text-ink-subtle">{title}</h2> : <span />}
      <button type="button" onClick={onToggle} className="wiki-icon-button" aria-label={collapsed ? `Open ${title}` : `Collapse ${title}`}>
        {collapsed ? collapsedIcon : expandedIcon}
      </button>
    </div>
  );
}

function ScopeChip({ scope, root, folder, page }: { scope: WikiScope; root: string; folder?: string; page?: string }) {
  const label = scope === 'root' ? root : scope === 'folder' ? folder ?? root : page ?? root;
  return (
    <span className="inline-flex max-w-full items-center gap-1.5 rounded-full border border-accent/30 bg-accent-soft px-2.5 py-1 text-xs font-medium text-ink">
      <span className="capitalize text-ink-muted">{scope}</span>
      <span className="max-w-[220px] truncate">{label}</span>
    </span>
  );
}

function RevisionItem({ label, detail, active = false }: { label: string; detail: string; active?: boolean }) {
  return (
    <li className={`rounded-xl border px-3 py-2 ${active ? 'border-accent/40 bg-accent-soft' : 'border-line bg-canvas-raised'}`}>
      <div className="flex items-center gap-2 text-sm font-medium text-ink">
        <Clock3 className="h-4 w-4" strokeWidth={1.8} />
        {label}
      </div>
      <p className="mt-0.5 text-[11px] text-ink-subtle">{detail}</p>
    </li>
  );
}