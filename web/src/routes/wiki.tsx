import { createFileRoute } from '@tanstack/react-router';
import { lazy, Suspense, useEffect, useRef, useState } from 'react';
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
  PencilLine,
  Plus,
  Save,
  Search,
  Send,
  Settings2,
  Sparkles,
  Trash2,
  Undo2,
  WandSparkles,
  X,
} from 'lucide-react';
import { useWikiStore, type WikiPageChatMessage, type WikiScope } from '../stores/wikiStore';
import type { WikiFolder, WikiPage, WikiRevision, WikiSearchResult } from '../lib/wikiApi';

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
  const [pageTitleDraft, setPageTitleDraft] = useState('');
  const [renamingRoot, setRenamingRoot] = useState(false);
  const [rootNameDraft, setRootNameDraft] = useState('');
  const [renamingFolderId, setRenamingFolderId] = useState<string | null>(null);
  const [folderNameDraft, setFolderNameDraft] = useState('');
  const pageTitleRef = useRef<HTMLInputElement>(null);
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
    searchResults,
    draft,
    pageChatMessages,
    pageChatDraft,
    selectedText,
    selectionRewriteDraft,
    dirty,
    loading,
    saving,
    searching,
    pageAssistantBusy,
    error,
    loadRoots,
    selectRoot,
    selectFolder,
    selectPage,
    createRoot,
    renameRoot,
    createFolder,
    renameFolder,
    moveFolder,
    createPage,
    savePage,
    renamePage,
    movePage,
    deletePage,
    discardDraft,
    restoreRevision,
    undoLatestAiEdit,
    askPage,
    draftPage,
    applyPageDraft,
    clearPageDraft,
    rewriteSelection,
    applySelectionRewrite,
    clearSelectionRewrite,
    setDraft,
    setSelectedText,
    setSearch,
    setScope,
  } = useWikiStore();

  useEffect(() => {
    void loadRoots();
  }, [loadRoots]);

  useEffect(() => {
    setPageTitleDraft(page?.page.title ?? '');
  }, [page?.page.id, page?.page.title]);

  const selectedRoot = roots.find((root) => root.id === selectedRootId) ?? tree?.root ?? null;
  const selectedPage = page?.page ?? tree?.pages.find((candidate) => candidate.id === selectedPageId) ?? null;
  const folders = tree?.folders ?? [];
  const selectedFolder = tree?.folders.find((folder) => folder.id === selectedFolderId) ?? null;
  const selectedFolderPath = selectedFolder ? formatFolderPath(folders, selectedFolder) : null;
  const filteredPages = tree?.pages ?? [];
  const rootPages = filteredPages.filter((candidate) => !candidate.folderId);
  const rootFolders = folders.filter((folder) => !folder.parentFolderId);
  const folderOptions = folders.map((folder) => ({ id: folder.id, label: formatFolderPath(folders, folder) }));
  const folderParentOptions = selectedFolder
    ? folders
        .filter((folder) => folder.id !== selectedFolder.id && !isFolderDescendant(folders, selectedFolder.id, folder.id))
        .map((folder) => ({ id: folder.id, label: formatFolderPath(folders, folder) }))
    : [];
  const hasSearch = search.trim().length > 0;
  const markdownWordCount = countWords(draft);
  const busy = loading || saving || pageAssistantBusy;
  const canUndoLatestAiEdit = Boolean(page && revisions[0]?.source === 'ai' && revisions[1]);
  useEffect(() => {
    if (!renamingRoot) setRootNameDraft(selectedRoot?.name ?? '');
  }, [renamingRoot, selectedRoot?.id, selectedRoot?.name]);

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
  const submitSelectionRewrite = async () => {
    const prompt = pagePrompt.trim();
    if (!prompt || !selectedText.trim()) return;
    setPagePrompt('');
    await rewriteSelection(prompt);
  };
  const submitPageTitle = async () => {
    const trimmed = pageTitleDraft.trim();
    if (!selectedPage || !trimmed || trimmed === selectedPage.title) {
      setPageTitleDraft(selectedPage?.title ?? '');
      return;
    }
    await renamePage(trimmed);
  };
  const beginRootRename = () => {
    if (!selectedRoot) return;
    setRootNameDraft(selectedRoot.name);
    setRenamingRoot(true);
  };
  const cancelRootRename = () => {
    setRenamingRoot(false);
    setRootNameDraft(selectedRoot?.name ?? '');
  };
  const submitRootRename = async () => {
    const trimmed = rootNameDraft.trim();
    if (!selectedRoot || !trimmed || trimmed === selectedRoot.name) {
      cancelRootRename();
      return;
    }
    await renameRoot(selectedRoot.id, trimmed);
    setRenamingRoot(false);
  };
  const beginFolderRename = (folder: WikiFolder) => {
    setRenamingFolderId(folder.id);
    setFolderNameDraft(folder.name);
  };
  const cancelFolderRename = () => {
    setRenamingFolderId(null);
    setFolderNameDraft('');
  };
  const submitFolderRename = async () => {
    if (!renamingFolderId) return;
    const folder = tree?.folders.find((candidate) => candidate.id === renamingFolderId) ?? null;
    const trimmed = folderNameDraft.trim();
    if (!folder || !trimmed || trimmed === folder.name) {
      cancelFolderRename();
      return;
    }
    await renameFolder(folder.id, trimmed);
    cancelFolderRename();
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
            {selectedPage ? (
              <input
                ref={pageTitleRef}
                value={pageTitleDraft}
                onChange={(event) => setPageTitleDraft(event.target.value)}
                onBlur={() => void submitPageTitle()}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    event.currentTarget.blur();
                  }
                }}
                disabled={busy || dirty}
                aria-label="Page title"
                className="min-w-0 max-w-[360px] rounded-lg border border-transparent bg-transparent px-1 py-0.5 text-xl font-semibold text-ink outline-none transition hover:border-line focus:border-accent focus:bg-canvas-raised focus:ring-2 focus:ring-accent/15 disabled:opacity-70"
              />
            ) : (
              <h1 className="truncate text-xl font-semibold text-ink">
                {selectedRoot?.name ?? 'Wiki'}
              </h1>
            )}
            <ScopeChip scope={scope} root={selectedRoot?.name} folder={selectedFolderPath ?? undefined} page={selectedPage?.title} />
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
          <button type="button" className="wiki-icon-button" title={scope === 'folder' ? 'New subfolder' : 'New folder'} aria-label={scope === 'folder' ? 'New subfolder' : 'New folder'} disabled={busy || !selectedRootId} onClick={() => void createFolder()}>
            <Folder className="h-4 w-4" strokeWidth={1.8} />
          </button>
          <button type="button" className="wiki-command-button" disabled={busy || !selectedRootId} onClick={() => void createPage()}>
            <Plus className="h-4 w-4" strokeWidth={1.9} />
            New page
          </button>
          {dirty ? (
            <button type="button" className="wiki-command-button" disabled={busy || !page} onClick={discardDraft}>
              <X className="h-4 w-4" strokeWidth={1.9} />
              Discard
            </button>
          ) : null}
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
              <div className="flex items-center justify-between gap-2">
                <label className="block text-xs font-medium text-ink-muted" htmlFor="wiki-root-select">
                  Root
                </label>
                <button
                  type="button"
                  className="flex h-7 w-7 items-center justify-center rounded-full text-ink-subtle transition hover:bg-canvas-raised hover:text-ink disabled:opacity-40"
                  title="Rename root"
                  aria-label="Rename root"
                  disabled={!selectedRoot || busy || dirty || renamingRoot}
                  onClick={beginRootRename}
                >
                  <PencilLine className="h-3.5 w-3.5" strokeWidth={1.8} />
                </button>
              </div>
              {renamingRoot ? (
                <input
                  autoFocus
                  value={rootNameDraft}
                  onChange={(event) => setRootNameDraft(event.target.value)}
                  onBlur={() => void submitRootRename()}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter') {
                      event.preventDefault();
                      event.currentTarget.blur();
                    }
                    if (event.key === 'Escape') {
                      event.preventDefault();
                      cancelRootRename();
                    }
                  }}
                  aria-label="Root name"
                  className="w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 text-sm text-ink outline-none transition focus:border-accent focus:ring-2 focus:ring-accent/15"
                />
              ) : (
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
              )}
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

              {tree && hasSearch ? (
                <SearchResultsList
                  results={searchResults}
                  searching={searching}
                  selectedPageId={selectedPageId}
                  onPageSelect={(pageId) => void selectPage(pageId)}
                />
              ) : tree ? (
                <nav className="space-y-3" aria-label="Wiki folders">
                  {rootFolders.map((folder) => (
                    <FolderSection
                      key={folder.id}
                      folder={folder}
                      folders={tree.folders}
                      pages={filteredPages}
                      selectedFolderId={selectedFolderId}
                      selectedPageId={selectedPageId}
                      scope={scope}
                      renamingFolderId={renamingFolderId}
                      folderNameDraft={folderNameDraft}
                      onFolderSelect={selectFolder}
                      onPageSelect={(pageId) => void selectPage(pageId)}
                      onFolderRenameStart={beginFolderRename}
                      onFolderRenameCancel={cancelFolderRename}
                      onFolderRenameSubmit={() => void submitFolderRename()}
                      onFolderNameDraftChange={setFolderNameDraft}
                    />
                  ))}
                  {rootPages.length > 0 ? (
                    <FolderSection
                      folder={null}
                      pages={rootPages}
                      folders={tree.folders}
                      selectedFolderId={selectedFolderId}
                      selectedPageId={selectedPageId}
                      scope={scope}
                      renamingFolderId={renamingFolderId}
                      folderNameDraft={folderNameDraft}
                      onFolderSelect={selectFolder}
                      onPageSelect={(pageId) => void selectPage(pageId)}
                      onFolderRenameStart={beginFolderRename}
                      onFolderRenameCancel={cancelFolderRename}
                      onFolderRenameSubmit={() => void submitFolderRename()}
                      onFolderNameDraftChange={setFolderNameDraft}
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
                  {selectedRoot?.name ?? 'Wiki'}{selectedFolderPath ? ` / ${selectedFolderPath}` : ''}
                </span>
              </div>
              <div className="flex items-center gap-1.5">
                {selectedFolder && scope === 'folder' ? (
                  <div className="relative hidden min-w-[176px] sm:block">
                    <select
                      aria-label="Folder parent"
                      value={selectedFolder.parentFolderId ?? ''}
                      disabled={busy || dirty}
                      onChange={(event) => void moveFolder(selectedFolder.id, event.target.value || null)}
                      className="w-full appearance-none rounded-lg border border-line bg-canvas-raised py-1.5 pl-2.5 pr-8 text-xs text-ink outline-none transition focus:border-accent focus:ring-2 focus:ring-accent/15 disabled:opacity-50"
                    >
                      <option value="">Root</option>
                      {folderParentOptions.map((folder) => (
                        <option key={folder.id} value={folder.id}>{folder.label}</option>
                      ))}
                    </select>
                    <ChevronDown className="pointer-events-none absolute right-2 top-2 h-3.5 w-3.5 text-ink-subtle" strokeWidth={1.8} />
                  </div>
                ) : page ? (
                  <div className="relative hidden min-w-[176px] sm:block">
                    <select
                      aria-label="Page folder"
                      value={page.page.folderId ?? ''}
                      disabled={busy || dirty}
                      onChange={(event) => void movePage(event.target.value || null)}
                      className="w-full appearance-none rounded-lg border border-line bg-canvas-raised py-1.5 pl-2.5 pr-8 text-xs text-ink outline-none transition focus:border-accent focus:ring-2 focus:ring-accent/15 disabled:opacity-50"
                    >
                      <option value="">Root</option>
                      {folderOptions.map((folder) => (
                        <option key={folder.id} value={folder.id}>{folder.label}</option>
                      ))}
                    </select>
                    <ChevronDown className="pointer-events-none absolute right-2 top-2 h-3.5 w-3.5 text-ink-subtle" strokeWidth={1.8} />
                  </div>
                ) : null}
                <button
                  type="button"
                  className="wiki-icon-button"
                  title="Edit page title"
                  aria-label="Edit page title"
                  disabled={!page || busy || dirty}
                  onClick={() => {
                    pageTitleRef.current?.focus();
                    pageTitleRef.current?.select();
                  }}
                >
                  <Settings2 className="h-4 w-4" strokeWidth={1.8} />
                </button>
                <button
                  type="button"
                  className="wiki-icon-button"
                  title="Delete page"
                  aria-label="Delete page"
                  disabled={!page || busy || dirty}
                  onClick={() => {
                    if (window.confirm(`Delete ${page?.page.title ?? 'this page'}?`)) {
                      void deletePage();
                    }
                  }}
                >
                  <Trash2 className="h-4 w-4" strokeWidth={1.8} />
                </button>
                <button type="button" className="wiki-icon-button" title="Undo latest AI edit" aria-label="Undo latest AI edit" disabled={busy || !canUndoLatestAiEdit} onClick={() => void undoLatestAiEdit()}>
                  <Undo2 className="h-4 w-4" strokeWidth={1.8} />
                </button>
              </div>
            </div>

            {selectedRoot ? (
              page ? (
                <Suspense fallback={<div className="flex flex-1 items-center justify-center text-sm text-ink-muted">Loading editor</div>}>
                  <WikiMarkdownEditor markdown={draft} disabled={busy} onChange={setDraft} onSelectionChange={setSelectedText} />
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
                  {selectionRewriteDraft ? (
                    <div className="rounded-xl border border-accent/30 bg-accent-soft p-3">
                      <div className="flex items-center justify-between gap-2">
                        <span className="text-xs font-semibold uppercase tracking-[0.08em] text-ink-subtle">Selection Rewrite</span>
                        <button type="button" className="wiki-icon-button h-7 w-7" aria-label="Clear selection rewrite" disabled={busy} onClick={clearSelectionRewrite}>
                          <X className="h-3.5 w-3.5" strokeWidth={1.8} />
                        </button>
                      </div>
                      <div className="mt-2 grid gap-2">
                        <pre className="max-h-24 overflow-y-auto whitespace-pre-wrap rounded-lg bg-canvas/70 p-2 text-[11px] leading-5 text-ink-subtle">{selectionRewriteDraft.selectedText}</pre>
                        <pre className="max-h-28 overflow-y-auto whitespace-pre-wrap rounded-lg bg-canvas p-2 text-[11px] leading-5 text-ink-muted">{selectionRewriteDraft.replacementText}</pre>
                      </div>
                      <button type="button" className="btn-primary mt-3 h-8 px-3 text-xs" disabled={busy || !page} onClick={() => void applySelectionRewrite()}>
                        Apply replacement
                      </button>
                    </div>
                  ) : null}
                  {selectedText ? (
                    <div className="rounded-xl border border-line bg-canvas px-3 py-2 text-xs text-ink-muted">
                      Selected {countWords(selectedText)} words
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
                    <button type="button" className="btn-quiet h-8 px-3 text-xs" disabled={!page || busy || !pagePrompt.trim() || !selectedText.trim()} onClick={() => void submitSelectionRewrite()}>
                      <WandSparkles className="h-3.5 w-3.5" strokeWidth={1.8} />
                      Rewrite selection
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
                        disabled={busy || dirty || index === 0}
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

function SearchResultsList({
  results,
  searching,
  selectedPageId,
  onPageSelect,
}: {
  results: WikiSearchResult[];
  searching: boolean;
  selectedPageId: string | null;
  onPageSelect: (pageId: string) => void;
}) {
  if (searching) {
    return (
      <div className="flex items-center gap-2 rounded-xl border border-line bg-canvas-raised px-3 py-4 text-sm text-ink-muted">
        <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.8} />
        Searching
      </div>
    );
  }

  if (results.length === 0) {
    return (
      <p className="rounded-xl border border-line bg-canvas-raised px-3 py-6 text-center text-sm text-ink-muted">
        No matching pages
      </p>
    );
  }

  return (
    <nav className="space-y-2" aria-label="Wiki search results">
      {results.map((result) => (
        <button
          key={result.pageId}
          type="button"
          onClick={() => onPageSelect(result.pageId)}
          className={`flex w-full items-start gap-2 rounded-xl border px-3 py-2 text-left transition ${selectedPageId === result.pageId ? 'border-accent/40 bg-accent-soft text-ink' : 'border-line bg-canvas-raised text-ink-muted hover:text-ink'}`}
        >
          <FileText className="mt-0.5 h-4 w-4 shrink-0" strokeWidth={1.8} />
          <span className="min-w-0">
            <span className="block truncate text-sm font-medium">{result.title}</span>
            <span className="mt-0.5 block truncate text-[11px] text-ink-subtle">{result.relativePath}</span>
            {result.excerpt ? <span className="mt-1 line-clamp-2 block text-xs leading-5">{result.excerpt}</span> : null}
          </span>
        </button>
      ))}
    </nav>
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
  folders,
  pages,
  selectedFolderId,
  selectedPageId,
  scope,
  renamingFolderId,
  folderNameDraft,
  onFolderSelect,
  onPageSelect,
  onFolderRenameStart,
  onFolderRenameCancel,
  onFolderRenameSubmit,
  onFolderNameDraftChange,
}: {
  folder: WikiFolder | null;
  folders: WikiFolder[];
  pages: WikiPage[];
  selectedFolderId: string | null;
  selectedPageId: string | null;
  scope: WikiScope;
  renamingFolderId: string | null;
  folderNameDraft: string;
  onFolderSelect: (folderId: string | null) => void;
  onPageSelect: (pageId: string) => void;
  onFolderRenameStart: (folder: WikiFolder) => void;
  onFolderRenameCancel: () => void;
  onFolderRenameSubmit: () => void;
  onFolderNameDraftChange: (name: string) => void;
}) {
  const isFolderSelected = folder ? selectedFolderId === folder.id && scope === 'folder' : !selectedFolderId && scope === 'root';
  const isRenaming = folder ? renamingFolderId === folder.id : false;
  const childFolders = folder ? folders.filter((candidate) => candidate.parentFolderId === folder.id) : [];
  const sectionPages = folder ? pages.filter((page) => page.folderId === folder.id) : pages;
  return (
    <section>
      {isRenaming ? (
        <div className="flex w-full items-center gap-2 rounded-lg bg-accent-soft px-2 py-1.5 text-xs font-medium text-ink">
          <Folder className="h-3.5 w-3.5 shrink-0" strokeWidth={1.8} />
          <input
            autoFocus
            value={folderNameDraft}
            onChange={(event) => onFolderNameDraftChange(event.target.value)}
            onBlur={onFolderRenameSubmit}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault();
                event.currentTarget.blur();
              }
              if (event.key === 'Escape') {
                event.preventDefault();
                onFolderRenameCancel();
              }
            }}
            aria-label="Folder name"
            className="min-w-0 flex-1 rounded-md border border-accent/30 bg-canvas-raised px-2 py-0.5 text-xs font-medium text-ink outline-none focus:border-accent focus:ring-2 focus:ring-accent/15"
          />
        </div>
      ) : (
        <div className={`flex w-full items-center gap-1 rounded-lg transition ${isFolderSelected ? 'bg-accent-soft text-ink' : 'text-ink-subtle hover:text-ink'}`}>
          <button
            type="button"
            onClick={() => onFolderSelect(folder?.id ?? null)}
            className="flex min-w-0 flex-1 items-center gap-2 px-2 py-1.5 text-left text-xs font-medium uppercase tracking-[0.08em]"
          >
            <Folder className="h-3.5 w-3.5 shrink-0" strokeWidth={1.8} />
            <span className="truncate">{folder?.name ?? 'Pages'}</span>
          </button>
          {folder && isFolderSelected ? (
            <button
              type="button"
              className="mr-1 flex h-6 w-6 shrink-0 items-center justify-center rounded-full text-ink-subtle transition hover:bg-canvas-raised hover:text-ink"
              title="Rename folder"
              aria-label="Rename folder"
              onClick={() => onFolderRenameStart(folder)}
            >
              <PencilLine className="h-3.5 w-3.5" strokeWidth={1.8} />
            </button>
          ) : null}
        </div>
      )}
      <ul className="mt-1 space-y-1">
        {sectionPages.map((page) => (
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
      {childFolders.length > 0 ? (
        <div className="mt-2 space-y-2 border-l border-line/70 pl-3">
          {childFolders.map((childFolder) => (
            <FolderSection
              key={childFolder.id}
              folder={childFolder}
              folders={folders}
              pages={pages}
              selectedFolderId={selectedFolderId}
              selectedPageId={selectedPageId}
              scope={scope}
              renamingFolderId={renamingFolderId}
              folderNameDraft={folderNameDraft}
              onFolderSelect={onFolderSelect}
              onPageSelect={onPageSelect}
              onFolderRenameStart={onFolderRenameStart}
              onFolderRenameCancel={onFolderRenameCancel}
              onFolderRenameSubmit={onFolderRenameSubmit}
              onFolderNameDraftChange={onFolderNameDraftChange}
            />
          ))}
        </div>
      ) : null}
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

function formatFolderPath(folders: WikiFolder[], folder: WikiFolder): string {
  const foldersById = new Map(folders.map((candidate) => [candidate.id, candidate]));
  const segments: string[] = [];
  const seen = new Set<string>();
  let current: WikiFolder | undefined = folder;

  while (current && !seen.has(current.id)) {
    seen.add(current.id);
    segments.push(current.name);
    current = current.parentFolderId ? foldersById.get(current.parentFolderId) : undefined;
  }

  return segments.reverse().join(' / ');
}

function isFolderDescendant(folders: WikiFolder[], ancestorId: string, folderId: string): boolean {
  const foldersById = new Map(folders.map((folder) => [folder.id, folder]));
  const seen = new Set<string>();
  let current = foldersById.get(folderId);

  while (current && !seen.has(current.id)) {
    seen.add(current.id);
    if (current.parentFolderId === ancestorId) return true;
    current = current.parentFolderId ? foldersById.get(current.parentFolderId) : undefined;
  }

  return false;
}