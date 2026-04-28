import { createFileRoute } from '@tanstack/react-router';
import { lazy, Suspense, useEffect, useMemo, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import {
  BookOpenText,
  ChevronDown,
  Circle,
  Clock3,
  FileText,
  Folder,
  Library,
  Loader2,
  PanelLeftClose,
  PanelLeftOpen,
  PanelRightClose,
  PanelRightOpen,
  Plus,
  Save,
  Search,
  Send,
  Settings2,
  Sparkles,
  Undo2,
  WandSparkles,
  X,
} from 'lucide-react';
import { useWikiStore, type WikiPageChatMessage, type WikiScope } from '../stores/wikiStore';
import type { WikiFolder, WikiPage, WikiRevision } from '../lib/wikiApi';

const WikiMarkdownEditor = lazy(() =>
  import('../components/wiki/WikiMarkdownEditor').then((module) => ({
    default: module.WikiMarkdownEditor,
  })),
);

export const Route = createFileRoute('/wiki')({
  component: WikiRoute,
});

function WikiRoute() {
  const [leftCollapsed, setLeftCollapsed] = useState(false);
  const [rightCollapsed, setRightCollapsed] = useState(false);
  const [pagePrompt, setPagePrompt] = useState('');
  const {
    roots,
    tree,
    page,
    revisions,
    selectedRootId,
    selectedFolderId,
    selectedPageId,
    scope,
    search,
    draft,
    pageChatMessages,
    pageChatDraft,
    dirty,
    loading,
    saving,
    pageAssistantBusy,
    error,
    loadRoots,
    selectRoot,
    selectFolder,
    selectPage,
    createRoot,
    createFolder,
    createPage,
    savePage,
    restoreRevision,
    askPage,
    draftPage,
    applyPageDraft,
    clearPageDraft,
    setDraft,
    setSearch,
    setScope,
  } = useWikiStore();

  useEffect(() => {
    void loadRoots();
  }, [loadRoots]);

  const selectedRoot = roots.find((root) => root.id === selectedRootId) ?? tree?.root ?? null;
  const selectedPage = page?.page ?? tree?.pages.find((candidate) => candidate.id === selectedPageId) ?? null;
  const selectedFolder = tree?.folders.find((folder) => folder.id === selectedFolderId) ?? null;
  const filteredPages = useMemo(() => {
    const pages = tree?.pages ?? [];
    const query = search.trim().toLowerCase();
    if (!query) return pages;
    return pages.filter((candidate) => {
      return candidate.title.toLowerCase().includes(query)
        || candidate.excerpt.toLowerCase().includes(query)
        || candidate.relativePath.toLowerCase().includes(query);
    });
  }, [search, tree?.pages]);
  const rootPages = filteredPages.filter((candidate) => !candidate.folderId);
  const markdownWordCount = countWords(draft);
  const busy = loading || saving || pageAssistantBusy;
  const submitPageAsk = async () => {
    const prompt = pagePrompt.trim();
    if (!prompt) return;
    setPagePrompt('');
    await askPage(prompt);
  };
  const submitPageDraft = async () => {
    const prompt = pagePrompt.trim();
    if (!prompt) return;
    setPagePrompt('');
    await draftPage(prompt);
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
            <h1 className="truncate text-xl font-semibold text-ink">
              {selectedPage?.title ?? selectedRoot?.name ?? 'Wiki'}
            </h1>
            <ScopeChip scope={scope} root={selectedRoot?.name} folder={selectedFolder?.name} page={selectedPage?.title} />
            <span className="inline-flex items-center gap-1 rounded-full border border-line px-2 py-0.5 text-[11px] text-ink-muted">
              <Circle className={`h-2 w-2 ${dirty ? 'fill-amber-500 text-amber-500' : 'fill-emerald-500 text-emerald-500'}`} />
              {dirty ? 'Unsaved' : 'Saved'}
            </span>
            {busy ? <Loader2 className="h-4 w-4 animate-spin text-ink-subtle" strokeWidth={1.8} /> : null}
          </div>
        </div>

        <div className="flex shrink-0 flex-wrap items-center gap-2">
          <button type="button" className="wiki-icon-button" title="New root" aria-label="New root" disabled={busy} onClick={() => void createRoot()}>
            <Library className="h-4 w-4" strokeWidth={1.8} />
          </button>
          <button type="button" className="wiki-icon-button" title="New folder" aria-label="New folder" disabled={busy || !selectedRootId} onClick={() => void createFolder()}>
            <Folder className="h-4 w-4" strokeWidth={1.8} />
          </button>
          <button type="button" className="wiki-command-button" disabled={busy || !selectedRootId} onClick={() => void createPage()}>
            <Plus className="h-4 w-4" strokeWidth={1.9} />
            New page
          </button>
          <button type="button" className="wiki-command-button" disabled={busy || !dirty || !page} onClick={() => void savePage()}>
            <Save className="h-4 w-4" strokeWidth={1.9} />
            Save
          </button>
        </div>
      </header>

      {error ? (
        <div className="border-b border-rose-500/20 bg-rose-500/10 px-4 py-2 text-sm text-rose-600 md:px-6" data-testid="wiki-error">
          {error}
        </div>
      ) : null}

      <div
        className="grid min-h-0 flex-1 grid-cols-1 md:grid-cols-[var(--wiki-left)_minmax(0,1fr)_var(--wiki-right)]"
        style={{
          '--wiki-left': leftCollapsed ? '56px' : '304px',
          '--wiki-right': rightCollapsed ? '56px' : '336px',
        } as CSSProperties}
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
                  value={selectedRootId ?? ''}
                  onChange={(event) => void selectRoot(event.target.value)}
                  disabled={busy || roots.length === 0}
                  className="w-full appearance-none rounded-xl border border-line bg-canvas-raised px-3 py-2 pr-8 text-sm text-ink outline-none transition focus:border-accent focus:ring-2 focus:ring-accent/15 disabled:opacity-50"
                >
                  {roots.length === 0 ? <option value="">No roots</option> : null}
                  {roots.map((root) => (
                    <option key={root.id} value={root.id}>{root.name}</option>
                  ))}
                </select>
                <ChevronDown className="pointer-events-none absolute right-2.5 top-2.5 h-4 w-4 text-ink-subtle" strokeWidth={1.8} />
              </div>
              <p className="truncate text-[11px] text-ink-subtle">{selectedRoot?.path ?? 'Local wiki library'}</p>

              <div className="relative">
                <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-ink-subtle" strokeWidth={1.8} />
                <input
                  type="search"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  placeholder="Search pages"
                  disabled={!tree}
                  className="w-full rounded-xl border border-line bg-canvas-raised py-2 pl-9 pr-3 text-sm text-ink outline-none transition placeholder:text-ink-subtle focus:border-accent focus:ring-2 focus:ring-accent/15 disabled:opacity-50"
                />
              </div>

              {tree ? (
                <nav className="space-y-3" aria-label="Wiki folders">
                  {tree.folders.map((folder) => (
                    <FolderSection
                      key={folder.id}
                      folder={folder}
                      pages={filteredPages.filter((candidate) => candidate.folderId === folder.id)}
                      selectedFolderId={selectedFolderId}
                      selectedPageId={selectedPageId}
                      scope={scope}
                      onFolderSelect={selectFolder}
                      onPageSelect={(pageId) => void selectPage(pageId)}
                    />
                  ))}
                  {rootPages.length > 0 ? (
                    <FolderSection
                      folder={null}
                      pages={rootPages}
                      selectedFolderId={selectedFolderId}
                      selectedPageId={selectedPageId}
                      scope={scope}
                      onFolderSelect={selectFolder}
                      onPageSelect={(pageId) => void selectPage(pageId)}
                    />
                  ) : null}
                  {tree.folders.length === 0 && tree.pages.length === 0 ? (
                    <p className="rounded-xl border border-line bg-canvas-raised px-3 py-6 text-center text-sm text-ink-muted">
                      No pages yet
                    </p>
                  ) : null}
                </nav>
              ) : (
                <button type="button" className="btn-quiet w-full justify-center" disabled={busy} onClick={() => void createRoot()}>
                  <Plus className="h-4 w-4" strokeWidth={1.8} />
                  New root
                </button>
              )}
            </div>
          ) : null}
        </aside>

        <main className="min-h-0 overflow-hidden">
          <div className="flex h-full min-h-[640px] flex-col">
            <div className="flex items-center justify-between border-b border-line px-4 py-2 md:px-5">
              <div className="flex min-w-0 items-center gap-2 text-xs text-ink-muted">
                <BookOpenText className="h-4 w-4 shrink-0" strokeWidth={1.8} />
                <span className="truncate">
                  {selectedRoot?.name ?? 'Wiki'}{selectedFolder ? ` / ${selectedFolder.name}` : ''}
                </span>
              </div>
              <div className="flex items-center gap-1.5">
                <button type="button" className="wiki-icon-button" title="Page settings" aria-label="Page settings" disabled={!page}>
                  <Settings2 className="h-4 w-4" strokeWidth={1.8} />
                </button>
                <button type="button" className="wiki-icon-button" title="Undo AI edit" aria-label="Undo AI edit" disabled={!page || revisions.every((revision) => revision.source !== 'ai')}>
                  <Undo2 className="h-4 w-4" strokeWidth={1.8} />
                </button>
              </div>
            </div>

            {selectedRoot ? (
              page ? (
                <Suspense fallback={<div className="flex flex-1 items-center justify-center text-sm text-ink-muted">Loading editor</div>}>
                  <WikiMarkdownEditor markdown={draft} disabled={busy} onChange={setDraft} />
                </Suspense>
              ) : (
                <div className="flex flex-1 items-center justify-center px-6 text-center text-sm text-ink-muted">
                  Create or select a page
                </div>
              )
            ) : (
              <div className="flex flex-1 flex-col items-center justify-center gap-3 px-6 text-center">
                <p className="text-base font-medium text-ink">No wiki roots yet</p>
                <button type="button" className="btn-primary" disabled={busy} onClick={() => void createRoot()}>
                  <Plus className="h-4 w-4" strokeWidth={1.9} />
                  New root
                </button>
              </div>
            )}

            <footer className="flex flex-wrap items-center justify-between gap-2 border-t border-line px-4 py-2 text-[11px] text-ink-subtle md:px-5">
              <span>{markdownWordCount} words</span>
              <span>{selectedPage ? `Version ${selectedPage.version} · ${formatStamp(selectedPage.updatedAt)}` : 'No page selected'}</span>
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
                      disabled={candidate === 'folder' ? !selectedFolder : candidate === 'page' ? !selectedPage : !selectedRoot}
                      className={`rounded-full border px-3 py-1 text-xs capitalize transition disabled:cursor-not-allowed disabled:opacity-40 ${scope === candidate ? 'border-accent bg-accent-soft text-ink' : 'border-line text-ink-muted hover:text-ink'}`}
                    >
                      {candidate}
                    </button>
                  ))}
                </div>
              </section>

              <section className="space-y-2">
                <h2 className="text-xs font-semibold uppercase tracking-[0.08em] text-ink-subtle">Page Chat</h2>
                <div className="space-y-3 rounded-xl border border-line bg-canvas-raised p-3">
                  <div className="max-h-52 space-y-2 overflow-y-auto pr-1">
                    {pageChatMessages.length > 0 ? (
                      pageChatMessages.map((message) => <PageChatBubble key={message.id} message={message} />)
                    ) : (
                      <div className="flex items-center gap-2 rounded-xl border border-line bg-canvas px-3 py-2 text-sm text-ink-subtle">
                        <Sparkles className="h-4 w-4 shrink-0" strokeWidth={1.8} />
                        Ask about this page
                      </div>
                    )}
                    {pageAssistantBusy ? (
                      <div className="flex items-center gap-2 text-xs text-ink-subtle">
                        <Loader2 className="h-3.5 w-3.5 animate-spin" strokeWidth={1.8} />
                        Thinking
                      </div>
                    ) : null}
                  </div>
                  {pageChatDraft ? (
                    <div className="rounded-xl border border-accent/30 bg-accent-soft p-3">
                      <div className="flex items-center justify-between gap-2">
                        <span className="text-xs font-semibold uppercase tracking-[0.08em] text-ink-subtle">Draft</span>
                        <button type="button" className="wiki-icon-button h-7 w-7" aria-label="Clear draft" disabled={busy} onClick={clearPageDraft}>
                          <X className="h-3.5 w-3.5" strokeWidth={1.8} />
                        </button>
                      </div>
                      <pre className="mt-2 max-h-36 overflow-y-auto whitespace-pre-wrap rounded-lg bg-canvas/70 p-2 text-[11px] leading-5 text-ink-muted">{pageChatDraft.markdown}</pre>
                      <button type="button" className="btn-primary mt-3 h-8 px-3 text-xs" disabled={busy || !page} onClick={() => void applyPageDraft()}>
                        Apply draft
                      </button>
                    </div>
                  ) : null}
                  <textarea
                    value={pagePrompt}
                    onChange={(event) => setPagePrompt(event.target.value)}
                    disabled={!page || busy}
                    rows={3}
                    placeholder="Ask about this page"
                    className="field-input min-h-20 resize-none"
                  />
                  <div className="flex flex-wrap gap-2">
                    <button type="button" className="btn-quiet h-8 px-3 text-xs" disabled={!page || busy || !pagePrompt.trim()} onClick={() => void submitPageAsk()}>
                      <Send className="h-3.5 w-3.5" strokeWidth={1.8} />
                      Ask
                    </button>
                    <button type="button" className="btn-quiet h-8 px-3 text-xs" disabled={!page || busy || !pagePrompt.trim()} onClick={() => void submitPageDraft()}>
                      <WandSparkles className="h-3.5 w-3.5" strokeWidth={1.8} />
                      Draft
                    </button>
                  </div>
                </div>
              </section>

              <section className="space-y-2">
                <h2 className="text-xs font-semibold uppercase tracking-[0.08em] text-ink-subtle">Revisions</h2>
                {revisions.length > 0 ? (
                  <ol className="space-y-2">
                    {revisions.map((revision, index) => (
                      <RevisionItem
                        key={revision.id}
                        revision={revision}
                        active={index === 0}
                        disabled={busy || index === 0}
                        onRestore={() => void restoreRevision(revision.id)}
                      />
                    ))}
                  </ol>
                ) : (
                  <p className="rounded-xl border border-line bg-canvas-raised px-3 py-4 text-sm text-ink-muted">No revisions</p>
                )}
              </section>
            </div>
          ) : null}
        </aside>
      </div>
    </section>
  );
}

function PageChatBubble({ message }: { message: WikiPageChatMessage }) {
  return (
    <div className={`rounded-xl px-3 py-2 text-sm ${message.role === 'user' ? 'bg-accent-soft text-ink' : 'bg-canvas text-ink-muted'}`}>
      <p className="whitespace-pre-wrap leading-5">{message.text}</p>
      <p className="mt-1 text-[10px] uppercase tracking-[0.08em] text-ink-subtle">{message.role}</p>
    </div>
  );
}

function FolderSection({
  folder,
  pages,
  selectedFolderId,
  selectedPageId,
  scope,
  onFolderSelect,
  onPageSelect,
}: {
  folder: WikiFolder | null;
  pages: WikiPage[];
  selectedFolderId: string | null;
  selectedPageId: string | null;
  scope: WikiScope;
  onFolderSelect: (folderId: string | null) => void;
  onPageSelect: (pageId: string) => void;
}) {
  const isFolderSelected = folder ? selectedFolderId === folder.id && scope === 'folder' : !selectedFolderId && scope === 'root';
  return (
    <section>
      <button
        type="button"
        onClick={() => onFolderSelect(folder?.id ?? null)}
        className={`flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-xs font-medium uppercase tracking-[0.08em] transition ${isFolderSelected ? 'bg-accent-soft text-ink' : 'text-ink-subtle hover:text-ink'}`}
      >
        <Folder className="h-3.5 w-3.5" strokeWidth={1.8} />
        <span className="truncate">{folder?.name ?? 'Pages'}</span>
      </button>
      <ul className="mt-1 space-y-1">
        {pages.map((page) => (
          <li key={page.id}>
            <button
              type="button"
              onClick={() => onPageSelect(page.id)}
              className={`flex w-full items-start gap-2 rounded-xl px-2 py-2 text-left transition ${selectedPageId === page.id ? 'bg-canvas-raised text-ink shadow-soft' : 'text-ink-muted hover:bg-canvas-raised/70 hover:text-ink'}`}
            >
              <FileText className="mt-0.5 h-4 w-4 shrink-0" strokeWidth={1.8} />
              <span className="min-w-0">
                <span className="block truncate text-sm font-medium">{page.title}</span>
                <span className="mt-0.5 block truncate text-[11px] text-ink-subtle">{formatStamp(page.updatedAt)}</span>
              </span>
            </button>
          </li>
        ))}
      </ul>
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
  collapsedIcon: ReactNode;
  expandedIcon: ReactNode;
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

function ScopeChip({ scope, root, folder, page }: { scope: WikiScope; root?: string; folder?: string; page?: string }) {
  const label = scope === 'root' ? root : scope === 'folder' ? folder ?? root : page ?? root;
  return (
    <span className="inline-flex max-w-full items-center gap-1.5 rounded-full border border-accent/30 bg-accent-soft px-2.5 py-1 text-xs font-medium text-ink">
      <span className="capitalize text-ink-muted">{scope}</span>
      <span className="max-w-[220px] truncate">{label ?? 'None'}</span>
    </span>
  );
}

function RevisionItem({
  revision,
  active = false,
  disabled,
  onRestore,
}: {
  revision: WikiRevision;
  active?: boolean;
  disabled: boolean;
  onRestore: () => void;
}) {
  return (
    <li className={`rounded-xl border px-3 py-2 ${active ? 'border-accent/40 bg-accent-soft' : 'border-line bg-canvas-raised'}`}>
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex items-center gap-2 text-sm font-medium text-ink">
            <Clock3 className="h-4 w-4" strokeWidth={1.8} />
            Version {revision.version}
          </div>
          <p className="mt-0.5 text-[11px] text-ink-subtle">
            {revision.source} · {formatStamp(revision.createdAt)}
          </p>
        </div>
        <button
          type="button"
          className="rounded-full border border-line px-2.5 py-1 text-[11px] font-medium text-ink-muted transition hover:bg-accent-soft hover:text-ink disabled:cursor-not-allowed disabled:opacity-40"
          disabled={disabled}
          onClick={onRestore}
        >
          Restore
        </button>
      </div>
    </li>
  );
}

function countWords(markdown: string): number {
  return markdown.trim().length === 0 ? 0 : markdown.trim().split(/\s+/).length;
}

function formatStamp(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  });
}