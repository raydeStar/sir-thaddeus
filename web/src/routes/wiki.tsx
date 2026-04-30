import { createFileRoute } from '@tanstack/react-router';
import { lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import {
  BookOpenText,
  ChevronDown,
  Circle,
  Clock3,
  Download,
  Eye,
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
  Tags,
  Trash2,
  Undo2,
  WandSparkles,
  X,
} from 'lucide-react';
import { useWikiStore, type WikiPageChatMessage, type WikiScope, type WikiSearchScope } from '../stores/wikiStore';
import type { WikiAssistantSource, WikiFolder, WikiPage, WikiPageGraph, WikiPageReference, WikiRevision, WikiSearchResult, WikiTrashItem } from '../lib/wikiApi';

const WikiMarkdownEditor = lazy(() =>
  import('../components/wiki/WikiMarkdownEditor').then((module) => ({
    default: module.WikiMarkdownEditor,
  })),
);

type PendingWikiAction =
  | { kind: 'deletePage'; title: string }
  | { kind: 'deleteFolder'; folder: WikiFolder }
  | { kind: 'purgeTrashItem'; item: WikiTrashItem };

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
  const [trashOpen, setTrashOpen] = useState(false);
  const [pendingAction, setPendingAction] = useState<PendingWikiAction | null>(null);
  // Folder id currently being hovered as a drop target during a drag, or
  // the sentinel '__root__' for the workspace-root drop zone. null when no
  // drag is in progress over a target.
  const [dropTargetFolderId, setDropTargetFolderId] = useState<string | null>(null);
  const [previewRevisionId, setPreviewRevisionId] = useState<string | null>(null);
  const pageTitleRef = useRef<HTMLInputElement>(null);
  const {
    roots,
    tree,
    page,
    pageGraph,
    revisions,
    selectedRootId,
    selectedFolderId,
    selectedPageId,
    isDraftPage,
    draftTitle,
    scope,
    searchScope,
    search,
    searchResults,
    trashItems,
    draft,
    pageChatMessages,
    selectedText,
    dirty,
    loading,
    saving,
    searching,
    trashLoading,
    pageAssistantBusy,
    error,
    loadRoots,
    selectRoot,
    selectFolder,
    selectPage,
    createRoot,
    renameRoot,
    deleteRoot,
    exportRoot,
    createFolder,
    renameFolder,
    moveFolder,
    deleteFolder,
    loadTrash,
    restoreTrashItem,
    purgeTrashItem,
    createPage,
    savePage,
    renamePage,
    movePage,
    deletePage,
    discardDraft,
    setDraftTitle,
    restoreRevision,
    undoLatestAiEdit,
    askPage,
    draftPage,
    rewriteSelection,
    setDraft,
    setSelectedText,
    setSearch,
    setSearchScope,
    setScope,
  } = useWikiStore();

  useEffect(() => {
    void loadRoots();
  }, [loadRoots]);

  useEffect(() => {
    if (trashOpen && selectedRootId) void loadTrash();
  }, [trashOpen, selectedRootId, loadTrash]);

  useEffect(() => {
    if (isDraftPage) {
      setPageTitleDraft(draftTitle);
    } else {
      setPageTitleDraft(page?.page.title ?? '');
    }
  }, [page?.page.id, page?.page.title, isDraftPage, draftTitle]);

  useEffect(() => {
    setPreviewRevisionId(null);
  }, [page?.page.id]);

  const selectedRoot = roots.find((root) => root.id === selectedRootId) ?? tree?.root ?? null;
  const selectedPage = page?.page ?? null;
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
  const quickSearchChips = useMemo(() => extractWikiQuickSearchChips(draft), [draft]);
  const hasSearch = search.trim().length > 0;
  const markdownWordCount = countWords(draft);
  const busy = loading || saving || pageAssistantBusy;
  const canUndoLatestAiEdit = Boolean(page && revisions[0]?.source === 'ai' && revisions[1]);
  const previewRevision = revisions.find((revision) => revision.id === previewRevisionId) ?? null;
  const chatScrollRef = useRef<HTMLDivElement>(null);
  const promptRef = useRef<HTMLTextAreaElement>(null);
  // Track the previous selection state so we only auto-focus the prompt at
  // the moment a selection is *acquired* — not on every re-render while a
  // selection happens to exist (which would steal focus while the user types).
  const hadSelectionRef = useRef(false);

  // Pop the assistant pane open the moment the user highlights text in the
  // editor — they almost certainly want to do something with it, and finding a
  // collapsed sidebar shut on top of their selection was the #1 confusion.
  useEffect(() => {
    if (selectedText.trim().length > 0 && rightCollapsed) {
      setRightCollapsed(false);
    }
  }, [selectedText, rightCollapsed]);

  // When a selection is *acquired* (transition from empty → non-empty),
  // move focus into the prompt textarea so the user has a clear visual signal
  // that the assistant pane is ready for instructions. The textarea is only
  // mounted when the right pane is expanded, so we wait until both conditions
  // hold before focusing.
  useEffect(() => {
    const hasSelection = selectedText.trim().length > 0;
    if (hasSelection && !hadSelectionRef.current && !rightCollapsed) {
      // Defer one tick so the freshly-expanded pane has mounted the textarea.
      const id = window.setTimeout(() => promptRef.current?.focus(), 0);
      hadSelectionRef.current = true;
      return () => window.clearTimeout(id);
    }
    hadSelectionRef.current = hasSelection;
    return undefined;
  }, [selectedText, rightCollapsed]);

  useEffect(() => {
    setPagePrompt('');
  }, [selectedRootId, selectedPageId]);

  // Keep the chat transcript pinned to the bottom as messages stream in so
  // users see the latest assistant reply without hunting through the pane.
  useEffect(() => {
    const el = chatScrollRef.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  }, [pageChatMessages.length, pageAssistantBusy]);

  useEffect(() => {
    if (!renamingRoot) setRootNameDraft(selectedRoot?.name ?? '');
  }, [renamingRoot, selectedRoot?.id, selectedRoot?.name]);

  // Cmd/Ctrl+S → save the current page when there are unsaved changes, or
  // commit a draft when the user has typed a title or content.
  useEffect(() => {
    const handler = (event: KeyboardEvent) => {
      if (!(event.key === 's' || event.key === 'S')) return;
      if (!(event.metaKey || event.ctrlKey)) return;
      if (event.shiftKey || event.altKey) return;
      event.preventDefault();
      if (busy) return;
      if (isDraftPage) {
        if (!draft.trim() && !draftTitle.trim() && !pageTitleDraft.trim()) return;
        void savePage();
        return;
      }
      if (!dirty || !page) return;
      void savePage();
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [busy, dirty, page, savePage, isDraftPage, draft, draftTitle, pageTitleDraft]);

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
    if (isDraftPage) {
      setDraftTitle(trimmed);
      setPageTitleDraft(trimmed);
      return;
    }
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
  const confirmRootDelete = () => {
    if (!selectedRoot) return;
    if (window.confirm(`Remove workspace ${selectedRoot.name} from Sir Thaddeus? Files stay on disk at ${selectedRoot.path}.`)) {
      void deleteRoot(selectedRoot.id);
    }
  };
  const handleExportRoot = async () => {
    if (!selectedRoot) return;
    const download = await exportRoot(selectedRoot.id);
    if (!download) return;
    triggerFileDownload(download.blob, download.fileName);
  };
  const beginFolderRename = (folder: WikiFolder) => {
    setRenamingFolderId(folder.id);
    setFolderNameDraft(folder.name);
  };
  // Create a folder and immediately drop the user into rename mode on it,
  // matching the VS Code 'New Folder' UX. The store action mutates state
  // synchronously after the await, so the new folder id is available on the
  // store snapshot once the promise settles.
  const handleCreateFolder = async () => {
    await createFolder();
    const state = useWikiStore.getState();
    const newId = state.selectedFolderId;
    if (!newId) return;
    const created = state.tree?.folders.find((candidate) => candidate.id === newId);
    if (created) {
      setRenamingFolderId(created.id);
      setFolderNameDraft(created.name);
    }
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
  const confirmFolderDelete = (folder: WikiFolder) => {
    setPendingAction({ kind: 'deleteFolder', folder });
  };
  const runPendingAction = () => {
    const action = pendingAction;
    setPendingAction(null);
    if (!action) return;
    if (action.kind === 'deletePage') {
      void deletePage();
      return;
    }
    if (action.kind === 'deleteFolder') {
      void deleteFolder(action.folder.id);
      return;
    }
    void purgeTrashItem(action.item);
  };
  const pendingDialog = pendingAction ? describePendingAction(pendingAction) : null;

  return (
    <>
    {/* Pin to exactly the viewport height under the 44px AppShell titlebar.
        A min-h here lets the page grow taller than the viewport, which makes
        AppShell's <main> scroll and pushes the right-pane chat composer off
        the bottom of the screen. */}
    <section className="flex h-[calc(100vh-2.75rem)] flex-col bg-canvas" data-testid="route-wiki">
      <header className="flex shrink-0 flex-col gap-3 border-b border-line px-4 py-4 md:flex-row md:items-start md:justify-between md:gap-6 md:px-6">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2 text-[10px] font-semibold uppercase tracking-[0.12em] text-ink-subtle">
            <Library className="h-3 w-3" strokeWidth={1.8} />
            Wiki Canvas
            {busy ? <Loader2 className="h-3 w-3 animate-spin" strokeWidth={1.8} /> : null}
          </div>
          {selectedPage || isDraftPage ? (
            <input
              ref={pageTitleRef}
              value={pageTitleDraft}
              onChange={(event) => {
                setPageTitleDraft(event.target.value);
                if (isDraftPage) setDraftTitle(event.target.value);
              }}
              onBlur={() => void submitPageTitle()}
              onKeyDown={(event) => {
                if (event.key === 'Enter') {
                  event.preventDefault();
                  event.currentTarget.blur();
                }
              }}
              disabled={busy || (!isDraftPage && dirty)}
              aria-label="Page title"
              placeholder={isDraftPage ? 'Untitled' : undefined}
              className="mt-1.5 block w-full min-w-0 rounded-lg border border-transparent bg-transparent px-1 py-0.5 text-2xl font-semibold leading-tight text-ink outline-none transition placeholder:text-ink-subtle placeholder:font-medium hover:border-line focus:border-accent focus:bg-canvas-raised focus:ring-2 focus:ring-accent/15 disabled:opacity-70"
            />
          ) : (
            <h1 className="mt-1.5 truncate text-2xl font-semibold leading-tight text-ink">
              {selectedRoot?.name ?? 'Wiki'}
            </h1>
          )}
          <div className="mt-2 flex min-w-0 flex-wrap items-center gap-2 text-xs text-ink-muted">
            {selectedRoot ? (
              <span className="inline-flex items-center gap-1.5 rounded-full border border-line px-2 py-0.5">
                <Library className="h-3 w-3 text-ink-subtle" strokeWidth={1.8} />
                <span className="font-medium text-ink">{selectedRoot.name}</span>
              </span>
            ) : null}
            {selectedFolderPath ? (
              <span className="inline-flex items-center gap-1.5 rounded-full border border-line px-2 py-0.5">
                <Folder className="h-3 w-3 text-ink-subtle" strokeWidth={1.8} />
                <span className="truncate text-ink">{selectedFolderPath}</span>
              </span>
            ) : null}
            {isDraftPage ? (
              <span className="inline-flex items-center gap-1.5 rounded-full border border-accent/40 bg-accent-soft px-2 py-0.5 font-medium text-ink" aria-live="polite">
                <Circle className="h-2 w-2 fill-accent text-accent" />
                Draft
              </span>
            ) : selectedPage ? (
              <span className="inline-flex items-center gap-1.5 rounded-full border border-line px-2 py-0.5" aria-live="polite">
                <Circle className={`h-2 w-2 ${dirty ? 'fill-amber-500 text-amber-500' : 'fill-emerald-500 text-emerald-500'}`} />
                {dirty ? 'Unsaved' : 'Saved'}
              </span>
            ) : null}
            {quickSearchChips.map((chip) => (
              <button
                key={`${chip.kind}:${chip.searchValue}`}
                type="button"
                className="inline-flex items-center gap-1.5 rounded-full border border-line px-2 py-0.5 text-ink-muted transition hover:border-accent/40 hover:bg-accent-soft hover:text-ink disabled:cursor-not-allowed disabled:opacity-50"
                disabled={!tree}
                onClick={() => setSearch(chip.searchValue)}
                title={`Search for ${chip.searchValue}`}
              >
                {chip.label}
              </button>
            ))}
          </div>
        </div>

        <div className="flex shrink-0 flex-wrap items-center gap-2 md:pt-5">
          <button type="button" className="wiki-icon-button" title="New workspace" aria-label="New workspace" disabled={busy} onClick={() => void createRoot()}>
            <Library className="h-4 w-4" strokeWidth={1.8} />
          </button>
          <button type="button" className="wiki-command-button" title="Export workspace" aria-label="Export workspace" disabled={busy || !selectedRootId} onClick={() => void handleExportRoot()}>
            <Download className="h-4 w-4" strokeWidth={1.8} />
            Export
          </button>
          <button type="button" className="wiki-icon-button" title={scope === 'folder' ? 'New subfolder' : 'New folder'} aria-label={scope === 'folder' ? 'New subfolder' : 'New folder'} disabled={busy || !selectedRootId} onClick={() => void handleCreateFolder()}>
            <Folder className="h-4 w-4" strokeWidth={1.8} />
          </button>
          <button type="button" className="wiki-command-button" disabled={busy || !selectedRootId} onClick={() => void createPage()}>
            <Plus className="h-4 w-4" strokeWidth={1.9} />
            New page
          </button>
          {dirty ? (
            <button type="button" className="wiki-command-button" disabled={busy || (!page && !isDraftPage)} onClick={discardDraft}>
              <X className="h-4 w-4" strokeWidth={1.9} />
              Discard
            </button>
          ) : null}
          {canUndoLatestAiEdit ? (
            <button type="button" className="wiki-command-button border-accent/40 bg-accent-soft text-ink" disabled={busy || dirty} onClick={() => void undoLatestAiEdit()}>
              <Undo2 className="h-4 w-4" strokeWidth={1.9} />
              Rollback
            </button>
          ) : null}
          <button
            type="button"
            className={`wiki-command-button ${(dirty || isDraftPage) && !busy && (page || (isDraftPage && (draft.trim() || pageTitleDraft.trim()))) ? 'border-accent bg-accent text-white hover:bg-accent hover:border-accent' : ''}`}
            disabled={busy || (isDraftPage ? !draft.trim() && !pageTitleDraft.trim() : !dirty || !page)}
            onClick={() => void savePage()}
            title="Save (Ctrl+S)"
            aria-label="Save"
          >
            <Save className="h-4 w-4" strokeWidth={1.9} />
            Save
            <kbd aria-hidden="true" className={`ml-1 hidden rounded border px-1 text-[10px] font-mono leading-4 sm:inline-flex ${(dirty || isDraftPage) && !busy && (page || (isDraftPage && (draft.trim() || pageTitleDraft.trim()))) ? 'border-white/40 text-white/80' : 'border-line text-ink-subtle'}`}>
              Ctrl+S
            </kbd>
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
        <aside className="min-h-0 border-b border-line bg-canvas md:border-b-0 md:border-r" aria-label="Page tree">
          <PanelHeader
            title="Pages"
            collapsed={leftCollapsed}
            onToggle={() => setLeftCollapsed((value) => !value)}
            collapsedIcon={<PanelLeftOpen className="h-4 w-4" strokeWidth={1.8} />}
            expandedIcon={<PanelLeftClose className="h-4 w-4" strokeWidth={1.8} />}
          />
          {!leftCollapsed ? (
            <div className="space-y-3 px-3 pb-4">
              <div className="flex items-center gap-1.5 rounded-xl border border-line bg-canvas-raised p-1.5">
                <Library className="h-4 w-4 shrink-0 text-ink-muted" strokeWidth={1.8} />
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
                    aria-label="Workspace name"
                    className="min-w-0 flex-1 rounded-lg border border-line bg-canvas px-2 py-1 text-sm font-medium text-ink outline-none transition focus:border-accent focus:ring-2 focus:ring-accent/15"
                  />
                ) : (
                  <div className="relative min-w-0 flex-1">
                    <select
                      id="wiki-root-select"
                      aria-label="Workspace"
                      value={selectedRootId ?? ''}
                      onChange={(event) => void selectRoot(event.target.value)}
                      disabled={busy || roots.length === 0}
                      className="w-full appearance-none truncate rounded-lg border border-transparent bg-transparent py-1 pl-1 pr-7 text-sm font-medium text-ink outline-none transition hover:border-line focus:border-accent focus:bg-canvas focus:ring-2 focus:ring-accent/15 disabled:opacity-50"
                    >
                      {roots.length === 0 ? <option value="">Loading workspace</option> : null}
                      {roots.map((root) => (
                        <option key={root.id} value={root.id}>{root.name}</option>
                      ))}
                    </select>
                    <ChevronDown className="pointer-events-none absolute right-1.5 top-2 h-3.5 w-3.5 text-ink-subtle" strokeWidth={1.8} />
                  </div>
                )}
                {!renamingRoot ? (
                  <>
                  <button
                    type="button"
                    className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg text-ink-subtle transition hover:bg-canvas hover:text-ink disabled:opacity-40"
                    title="Rename workspace"
                    aria-label="Rename workspace"
                    disabled={!selectedRoot || busy || dirty || renamingRoot}
                    onClick={beginRootRename}
                  >
                    <PencilLine className="h-3.5 w-3.5" strokeWidth={1.8} />
                  </button>
                  <button
                    type="button"
                    className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg text-ink-subtle transition hover:bg-rose-500/10 hover:text-rose-600 disabled:opacity-40"
                    title="Remove workspace"
                    aria-label="Remove workspace"
                    disabled={!selectedRoot || busy || dirty || renamingRoot}
                    onClick={confirmRootDelete}
                  >
                    <Trash2 className="h-3.5 w-3.5" strokeWidth={1.8} />
                  </button>
                  </>
                ) : null}
              </div>

              <div className="grid grid-cols-2 gap-2">
                <button type="button" className="wiki-command-button justify-center px-2" disabled={busy || !selectedRootId} onClick={() => void createPage()}>
                  <Plus className="h-4 w-4" strokeWidth={1.9} />
                  Page
                </button>
                <button type="button" className="wiki-command-button justify-center px-2" disabled={busy || !selectedRootId} onClick={() => void handleCreateFolder()}>
                  <Folder className="h-4 w-4" strokeWidth={1.8} />
                  Folder
                </button>
              </div>

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
              {roots.length > 1 ? (
                <div className="grid grid-cols-2 rounded-xl border border-line bg-canvas-raised p-1" aria-label="Search scope">
                  {(['root', 'all'] as WikiSearchScope[]).map((candidate) => (
                    <button
                      key={candidate}
                      type="button"
                      className={`rounded-lg px-3 py-1.5 text-xs font-medium transition ${searchScope === candidate ? 'bg-accent-soft text-ink shadow-soft' : 'text-ink-muted hover:text-ink'}`}
                      disabled={!tree}
                      onClick={() => setSearchScope(candidate)}
                    >
                      {candidate === 'root' ? 'This workspace' : 'All'}
                    </button>
                  ))}
                </div>
              ) : null}

              {tree && hasSearch ? (
                <SearchResultsList
                  results={searchResults}
                  roots={roots}
                  searchScope={searchScope}
                  searching={searching}
                  selectedPageId={selectedPageId}
                  onPageSelect={(pageId) => void selectPage(pageId)}
                />
              ) : tree ? (
                <nav className="space-y-1" aria-label="Pages">
                  {selectedRoot ? (
                    <RootRow
                      name={selectedRoot.name}
                      selected={scope === 'root' && !selectedFolderId}
                      isDropTarget={dropTargetFolderId === '__root__'}
                      onSelect={() => {
                        selectFolder(null);
                        setScope('root');
                      }}
                      onDragOver={(event) => {
                        if (!event.dataTransfer.types.includes('application/x-wiki-folder-id')) return;
                        event.preventDefault();
                        event.dataTransfer.dropEffect = 'move';
                        setDropTargetFolderId('__root__');
                      }}
                      onDragLeave={() => setDropTargetFolderId((current) => (current === '__root__' ? null : current))}
                      onDrop={(event) => {
                        const draggedId = event.dataTransfer.getData('application/x-wiki-folder-id');
                        setDropTargetFolderId(null);
                        if (!draggedId) return;
                        event.preventDefault();
                        void moveFolder(draggedId, null);
                      }}
                    />
                  ) : null}
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
                      dropTargetFolderId={dropTargetFolderId}
                      onFolderSelect={selectFolder}
                      onPageSelect={(pageId) => void selectPage(pageId)}
                      onFolderRenameStart={beginFolderRename}
                      onFolderDelete={confirmFolderDelete}
                      onFolderRenameCancel={cancelFolderRename}
                      onFolderRenameSubmit={() => void submitFolderRename()}
                      onFolderNameDraftChange={setFolderNameDraft}
                      onFolderDragStart={(event, dragged) => {
                        event.dataTransfer.setData('application/x-wiki-folder-id', dragged.id);
                        event.dataTransfer.effectAllowed = 'move';
                      }}
                      onFolderDragOver={(event, target) => {
                        if (!event.dataTransfer.types.includes('application/x-wiki-folder-id')) return;
                        event.preventDefault();
                        event.dataTransfer.dropEffect = 'move';
                        setDropTargetFolderId(target.id);
                      }}
                      onFolderDragLeave={(target) => setDropTargetFolderId((current) => (current === target.id ? null : current))}
                      onFolderDrop={(event, target) => {
                        const draggedId = event.dataTransfer.getData('application/x-wiki-folder-id');
                        setDropTargetFolderId(null);
                        if (!draggedId || draggedId === target.id) return;
                        // Block dropping a folder onto its own descendant — that would orphan the subtree.
                        if (isFolderDescendant(tree.folders, draggedId, target.id)) return;
                        event.preventDefault();
                        void moveFolder(draggedId, target.id);
                      }}
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
                      dropTargetFolderId={dropTargetFolderId}
                      onFolderSelect={selectFolder}
                      onPageSelect={(pageId) => void selectPage(pageId)}
                      onFolderRenameStart={beginFolderRename}
                      onFolderDelete={confirmFolderDelete}
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
                <div className="rounded-xl border border-line bg-canvas-raised px-3 py-4 text-center text-sm text-ink-muted">
                  Loading pages
                </div>
              )}
              <TrashPanel
                open={trashOpen}
                items={trashItems}
                loading={trashLoading}
                disabled={busy || dirty || !selectedRootId}
                onOpenChange={setTrashOpen}
                onRestore={(item) => void restoreTrashItem(item)}
                onPurge={(item) => setPendingAction({ kind: 'purgeTrashItem', item })}
              />
            </div>
          ) : null}
        </aside>

        <main className="min-h-0 overflow-hidden">
          <div className="flex h-full min-h-0 flex-col">
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
                  onClick={() => setPendingAction({ kind: 'deletePage', title: page?.page.title ?? 'this page' })}
                >
                  <Trash2 className="h-4 w-4" strokeWidth={1.8} />
                </button>
                <button type="button" className="wiki-icon-button" title="Rollback latest AI edit" aria-label="Rollback latest AI edit" disabled={busy || dirty || !canUndoLatestAiEdit} onClick={() => void undoLatestAiEdit()}>
                  <Undo2 className="h-4 w-4" strokeWidth={1.8} />
                </button>
              </div>
            </div>

            {selectedRoot ? (
              page || isDraftPage ? (
                <Suspense fallback={<div className="flex flex-1 items-center justify-center text-sm text-ink-muted">Loading editor</div>}>
                  <WikiMarkdownEditor key={page ? `${page.page.id}:${page.page.version}` : 'draft'} markdown={draft} disabled={busy} onChange={setDraft} onSelectionChange={setSelectedText} />
                </Suspense>
              ) : (
                <div className="flex flex-1 items-center justify-center px-6 text-center">
                  {loading || selectedPageId ? (
                    <div className="flex items-center gap-2 text-sm text-ink-muted">
                      <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.8} />
                      Opening page
                    </div>
                  ) : (
                    <div className="flex max-w-xs flex-col items-center gap-3">
                      <div className="flex h-11 w-11 items-center justify-center rounded-2xl border border-line bg-canvas-raised text-ink-muted">
                        <FileText className="h-5 w-5" strokeWidth={1.8} />
                      </div>
                      <p className="text-sm font-medium text-ink">No page selected</p>
                      <button type="button" className="wiki-command-button" disabled={busy || !selectedRootId} onClick={() => void createPage()}>
                        <Plus className="h-4 w-4" strokeWidth={1.9} />
                        New page
                      </button>
                    </div>
                  )}
                </div>
              )
            ) : (
              <div className="flex flex-1 flex-col items-center justify-center gap-3 px-6 text-center">
                <p className="text-base font-medium text-ink">Preparing your wiki</p>
                <button type="button" className="btn-primary" disabled={busy} onClick={() => void createRoot()}>
                  <Plus className="h-4 w-4" strokeWidth={1.9} />
                  New workspace
                </button>
              </div>
            )}

            <footer className="flex flex-wrap items-center justify-between gap-2 border-t border-line px-4 py-2 text-[11px] text-ink-subtle md:px-5">
              <span>{markdownWordCount} words</span>
              <span>{selectedPage ? `Version ${selectedPage.version} · ${formatStamp(selectedPage.updatedAt)}` : isDraftPage ? 'New page · not saved' : 'No page selected'}</span>
            </footer>
          </div>
        </main>

        <aside className="flex min-h-0 flex-col border-t border-line bg-canvas md:border-l md:border-t-0" aria-label="Page chat and revisions">
          <PanelHeader
            title="Assistant"
            collapsed={rightCollapsed}
            onToggle={() => setRightCollapsed((value) => !value)}
            collapsedIcon={<PanelRightOpen className="h-4 w-4" strokeWidth={1.8} />}
            expandedIcon={<PanelRightClose className="h-4 w-4" strokeWidth={1.8} />}
          />
          {!rightCollapsed ? (
            <div className="flex min-h-0 flex-1 flex-col">
              {page ? (
                <>
                <details className="shrink-0 border-b border-line" open={false}>
                  <summary className="cursor-pointer list-none px-4 py-2.5 text-xs font-semibold uppercase tracking-[0.08em] text-ink-subtle hover:text-ink">
                    Revisions <span className="ml-1 normal-case tracking-normal text-ink-subtle">({revisions.length})</span>
                  </summary>
                  <div className="max-h-48 space-y-2 overflow-y-auto px-4 pb-4">
                    {revisions.length > 0 ? (
                      <ol className="space-y-2">
                        {revisions.map((revision, index) => (
                          <RevisionItem
                            key={revision.id}
                            revision={revision}
                            active={index === 0}
                            previewing={previewRevisionId === revision.id}
                            disabled={busy || dirty || index === 0}
                            onPreview={() => setPreviewRevisionId((current) => (current === revision.id ? null : revision.id))}
                            onRestore={() => void restoreRevision(revision.id)}
                          />
                        ))}
                      </ol>
                    ) : (
                      <p className="rounded-xl border border-line bg-canvas-raised px-3 py-4 text-sm text-ink-muted">No revisions</p>
                    )}
                    {previewRevision ? (
                      <RevisionPreview
                        revision={previewRevision}
                        currentMarkdown={draft}
                        onClose={() => setPreviewRevisionId(null)}
                      />
                    ) : null}
                  </div>
                </details>
                <WikiKnowledgePanel
                  graph={pageGraph}
                  onPageSelect={(pageId) => void selectPage(pageId)}
                  onTagSelect={(tag) => {
                    setSearch(`#${tag}`);
                    setSearchScope('root');
                  }}
                />
                </>
              ) : null}
              {selectedRoot ? (
                <section className="flex min-h-0 flex-1 flex-col" aria-label="Page chat">
                  <div ref={chatScrollRef} aria-live="polite" className="flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto px-4 py-3">
                    {pageChatMessages.length > 0 ? (
                      pageChatMessages.map((message) => <PageChatBubble key={message.id} message={message} onSourceSelect={(pageId) => void selectPage(pageId)} />)
                    ) : (
                      <p className="text-xs text-ink-subtle">
                        {!page
                          ? 'Tell Sir Thaddeus what this page should be about. Hit Enter and the page is created with whatever it writes — nothing saves until then.'
                          : selectedText.trim()
                            ? 'Tell Sir Thaddeus how to rewrite the highlighted passage.'
                            : 'Ask anything about this page, or highlight text to rewrite it.'}
                      </p>
                    )}
                    {pageAssistantBusy ? (
                      <div className="flex items-center gap-2 text-xs text-ink-subtle">
                        <Loader2 className="h-3.5 w-3.5 animate-spin" strokeWidth={1.8} />
                        Thinking
                      </div>
                    ) : null}
                  </div>
                  <div className="shrink-0 space-y-2 border-t border-line bg-canvas px-3 pt-3 pb-3">
                    <div className="grid grid-cols-3 rounded-xl border border-line bg-canvas-raised p-1" aria-label="Wiki assistant scope">
                      {(['root', 'folder', 'page'] as WikiScope[]).map((candidate) => {
                        const unavailable = candidate === 'folder' ? !selectedFolder : candidate === 'page' ? !page : false;
                        return (
                          <button
                            key={candidate}
                            type="button"
                            className={`rounded-lg border px-2 py-1.5 text-xs font-medium transition ${scope === candidate ? 'border-accent bg-accent-soft text-ink shadow-soft' : 'border-transparent text-ink-muted hover:text-ink'}`}
                            disabled={busy || unavailable}
                            onClick={() => setScope(candidate)}
                          >
                            {candidate}
                          </button>
                        );
                      })}
                    </div>
                    {selectedText ? (
                      <div className="rounded-lg border border-accent bg-accent-soft px-3 py-2">
                        <div className="flex items-start justify-between gap-2">
                          <p className="text-[10px] font-semibold uppercase tracking-[0.08em] text-accent">
                            Selected · {countWords(selectedText)} words
                          </p>
                          <button
                            type="button"
                            className="shrink-0 rounded p-0.5 text-ink-subtle hover:bg-canvas hover:text-ink"
                            onClick={() => setSelectedText('')}
                            title="Clear selection"
                            aria-label="Clear selection"
                          >
                            <X className="h-3 w-3" strokeWidth={2} />
                          </button>
                        </div>
                        <p className="mt-1 line-clamp-3 text-xs italic text-ink">
                          “{truncateForPreview(selectedText)}”
                        </p>
                      </div>
                    ) : null}
                    <textarea
                      ref={promptRef}
                      value={pagePrompt}
                      onChange={(event) => setPagePrompt(event.target.value)}
                      onKeyDown={(event) => {
                        // Guard against IME composition: Japanese/Chinese input
                        // methods fire Enter to confirm a candidate; submitting
                        // there would steal the keystroke from the IME.
                        if (event.nativeEvent.isComposing) return;
                        if (event.key === 'Enter' && !event.shiftKey) {
                          event.preventDefault();
                          if (busy || !pagePrompt.trim()) return;
                          if (selectedText.trim()) {
                            void submitSelectionRewrite();
                          } else if (!page) {
                            // Draft mode: the prompt creates the page itself.
                            void submitPageDraft();
                          } else {
                            void submitPageAsk();
                          }
                        }
                      }}
                      disabled={busy}
                      rows={2}
                      placeholder={!page ? 'Describe what this page should be about…' : selectedText.trim() ? 'How should this be rewritten?' : 'Ask about this page'}
                      aria-label="Page chat prompt"
                      className="field-input min-h-16 resize-none"
                    />
                    <div className="flex items-center justify-between gap-2">
                      <div className="flex items-center gap-1.5 text-[11px] text-ink-subtle">
                        {!selectedText.trim() ? (
                          <button
                            type="button"
                            className="rounded px-1.5 py-0.5 hover:bg-canvas hover:text-ink disabled:cursor-not-allowed disabled:opacity-55"
                            disabled={busy || !pagePrompt.trim()}
                            onClick={() => void submitPageDraft()}
                            title="Draft a new section into the page"
                          >
                            <WandSparkles className="mr-1 inline h-3 w-3" strokeWidth={1.8} />
                            Write
                          </button>
                        ) : null}
                      </div>
                      <button
                        type="button"
                        className="inline-flex items-center gap-1.5 rounded-lg bg-accent px-3 py-1.5 text-xs font-medium text-white shadow-sm transition hover:opacity-90 disabled:cursor-not-allowed disabled:bg-line-strong disabled:text-ink-subtle"
                        disabled={busy || !pagePrompt.trim()}
                        onClick={() => {
                          if (selectedText.trim()) {
                            void submitSelectionRewrite();
                          } else if (!page) {
                            void submitPageDraft();
                          } else {
                            void submitPageAsk();
                          }
                        }}
                      >
                        {selectedText.trim() ? (
                          <>
                            <WandSparkles className="h-3.5 w-3.5" strokeWidth={1.9} />
                            Rewrite selection
                          </>
                        ) : !page ? (
                          <>
                            <WandSparkles className="h-3.5 w-3.5" strokeWidth={1.9} />
                            Create
                          </>
                        ) : (
                          <>
                            <Send className="h-3.5 w-3.5" strokeWidth={1.9} />
                            Ask
                          </>
                        )}
                      </button>
                    </div>
                  </div>
                </section>
              ) : (
                <div className="mx-4 mt-4 rounded-xl border border-line bg-canvas-raised px-3 py-7 text-center">
                  <FileText className="mx-auto h-5 w-5 text-ink-subtle" strokeWidth={1.8} />
                  <p className="mt-2 text-sm font-medium text-ink">No workspace</p>
                </div>
              )}
            </div>
          ) : null}
        </aside>
      </div>
    </section>
    {pendingDialog ? (
      <WikiConfirmDialog
        title={pendingDialog.title}
        body={pendingDialog.body}
        confirmLabel={pendingDialog.confirmLabel}
        destructive={pendingDialog.destructive}
        disabled={busy}
        onCancel={() => setPendingAction(null)}
        onConfirm={runPendingAction}
      />
    ) : null}
    </>
  );
}

function triggerFileDownload(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.style.display = 'none';
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 0);
}

function SearchResultsList({
  results,
  roots,
  searchScope,
  searching,
  selectedPageId,
  onPageSelect,
}: {
  results: WikiSearchResult[];
  roots: Array<{ id: string; name: string }>;
  searchScope: WikiSearchScope;
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

  const rootNames = new Map(roots.map((root) => [root.id, root.name]));

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
            <span className="mt-0.5 block truncate text-[11px] text-ink-muted">
              {searchScope === 'all' ? `${rootNames.get(result.rootId) ?? 'Wiki'} / ` : ''}{result.relativePath}
            </span>
            {result.excerpt ? <span className="mt-1 line-clamp-2 block text-xs leading-5">{result.excerpt}</span> : null}
          </span>
        </button>
      ))}
    </nav>
  );
}

function TrashPanel({
  open,
  items,
  loading,
  disabled,
  onOpenChange,
  onRestore,
  onPurge,
}: {
  open: boolean;
  items: WikiTrashItem[];
  loading: boolean;
  disabled: boolean;
  onOpenChange: (open: boolean) => void;
  onRestore: (item: WikiTrashItem) => void;
  onPurge: (item: WikiTrashItem) => void;
}) {
  return (
    <section className="rounded-xl border border-line bg-canvas-raised">
      <button
        type="button"
        className="flex w-full items-center justify-between gap-2 px-3 py-2 text-left text-sm font-medium text-ink transition hover:bg-canvas/70 disabled:cursor-not-allowed disabled:opacity-55"
        disabled={disabled && !open}
        onClick={() => onOpenChange(!open)}
        aria-expanded={open}
      >
        <span className="flex min-w-0 items-center gap-2">
          <Trash2 className="h-4 w-4 shrink-0 text-ink-subtle" strokeWidth={1.8} />
          <span>Trash</span>
        </span>
        <span className="text-xs text-ink-subtle">{loading ? '...' : items.length}</span>
      </button>
      {open ? (
        <div className="space-y-2 border-t border-line px-2 py-2">
          {loading ? (
            <div className="flex items-center gap-2 px-2 py-3 text-sm text-ink-muted">
              <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.8} />
              Loading
            </div>
          ) : items.length === 0 ? (
            <p className="px-2 py-3 text-sm text-ink-muted">Empty</p>
          ) : (
            items.map((item) => (
              <div key={`${item.type}:${item.id}`} className="rounded-lg border border-line bg-canvas px-2 py-2">
                <div className="flex items-start gap-2">
                  {item.type === 'folder' ? (
                    <Folder className="mt-0.5 h-4 w-4 shrink-0 text-ink-subtle" strokeWidth={1.8} />
                  ) : (
                    <FileText className="mt-0.5 h-4 w-4 shrink-0 text-ink-subtle" strokeWidth={1.8} />
                  )}
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium text-ink">{item.name}</p>
                    <p className="mt-0.5 truncate text-[11px] text-ink-subtle">{item.relativePath}</p>
                    {item.type === 'folder' ? (
                      <p className="mt-1 text-[11px] text-ink-muted">
                        {item.folderCount} folders / {item.pageCount} pages
                      </p>
                    ) : null}
                  </div>
                </div>
                <div className="mt-2 flex items-center justify-end gap-1.5">
                  <button
                    type="button"
                    className="wiki-icon-button h-7 w-7"
                    title="Restore"
                    aria-label={`Restore ${item.name}`}
                    disabled={disabled}
                    onClick={() => onRestore(item)}
                  >
                    <Undo2 className="h-3.5 w-3.5" strokeWidth={1.8} />
                  </button>
                  <button
                    type="button"
                    className="wiki-icon-button h-7 w-7 text-rose-600 hover:bg-rose-500/10"
                    title="Delete forever"
                    aria-label={`Delete ${item.name} forever`}
                    disabled={disabled}
                    onClick={() => onPurge(item)}
                  >
                    <Trash2 className="h-3.5 w-3.5" strokeWidth={1.8} />
                  </button>
                </div>
              </div>
            ))
          )}
        </div>
      ) : null}
    </section>
  );
}

function WikiConfirmDialog({
  title,
  body,
  confirmLabel,
  destructive = false,
  disabled,
  onCancel,
  onConfirm,
}: {
  title: string;
  body: string;
  confirmLabel: string;
  destructive?: boolean;
  disabled: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/35 px-4" role="presentation" onMouseDown={onCancel}>
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="wiki-confirm-title"
        className="w-full max-w-sm rounded-xl border border-line bg-canvas p-4 shadow-xl"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <h2 id="wiki-confirm-title" className="text-base font-semibold text-ink">{title}</h2>
        <p className="mt-2 text-sm leading-5 text-ink-muted">{body}</p>
        <div className="mt-4 flex justify-end gap-2">
          <button type="button" className="wiki-command-button" disabled={disabled} onClick={onCancel}>
            Cancel
          </button>
          <button
            type="button"
            className={`wiki-command-button ${destructive ? 'border-rose-500 bg-rose-600 text-white hover:bg-rose-600 hover:border-rose-500' : 'border-accent bg-accent text-white hover:bg-accent hover:border-accent'}`}
            disabled={disabled}
            onClick={onConfirm}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}

function describePendingAction(action: PendingWikiAction): {
  title: string;
  body: string;
  confirmLabel: string;
  destructive: boolean;
} {
  if (action.kind === 'deletePage') {
    return {
      title: 'Move page to trash',
      body: `${action.title} will leave the page tree and can be restored from Trash. Its Markdown file and revisions stay on disk.`,
      confirmLabel: 'Move to trash',
      destructive: false,
    };
  }

  if (action.kind === 'deleteFolder') {
    return {
      title: 'Move folder to trash',
      body: `${action.folder.name} and everything inside it will leave the page tree and can be restored from Trash.`,
      confirmLabel: 'Move to trash',
      destructive: false,
    };
  }

  return {
    title: 'Delete forever',
    body: `${action.item.name} will be removed from disk with its stored revisions. This cannot be restored from Trash.`,
    confirmLabel: 'Delete forever',
    destructive: true,
  };
}

function WikiKnowledgePanel({
  graph,
  onPageSelect,
  onTagSelect,
}: {
  graph: WikiPageGraph | null;
  onPageSelect: (pageId: string) => void;
  onTagSelect: (tag: string) => void;
}) {
  if (!graph) return null;
  const hasTags = graph.tags.length > 0;
  const hasLinks = graph.links.length > 0;
  const hasBacklinks = graph.backlinks.length > 0;

  return (
    <section className="shrink-0 space-y-3 border-b border-line px-4 py-3" data-testid="wiki-knowledge-panel">
      <div className="flex items-center justify-between gap-2">
        <h3 className="text-xs font-semibold uppercase tracking-[0.08em] text-ink-subtle">Knowledge</h3>
        <span className="text-[11px] text-ink-subtle">{graph.tags.length + graph.links.length + graph.backlinks.length}</span>
      </div>
      {hasTags ? (
        <div className="space-y-1.5">
          <div className="flex items-center gap-1.5 text-[11px] font-medium uppercase tracking-[0.06em] text-ink-subtle">
            <Tags className="h-3.5 w-3.5" strokeWidth={1.8} />
            Tags
          </div>
          <div className="flex flex-wrap gap-1.5">
            {graph.tags.map((tag) => (
              <button
                key={tag}
                type="button"
                className="inline-flex max-w-full items-center rounded-full border border-line bg-canvas-raised px-2 py-1 text-[11px] font-medium text-ink-muted transition hover:border-accent/40 hover:bg-accent-soft hover:text-ink"
                onClick={() => onTagSelect(tag)}
                title={`Search #${tag}`}
              >
                #{tag}
              </button>
            ))}
          </div>
        </div>
      ) : null}
      {hasLinks ? <WikiPageReferenceGroup title="Links" references={graph.links} onPageSelect={onPageSelect} /> : null}
      {hasBacklinks ? <WikiPageReferenceGroup title="Backlinks" references={graph.backlinks} onPageSelect={onPageSelect} /> : null}
      {!hasTags && !hasLinks && !hasBacklinks ? (
        <p className="text-xs text-ink-subtle">No links or tags</p>
      ) : null}
    </section>
  );
}

function WikiPageReferenceGroup({
  title,
  references,
  onPageSelect,
}: {
  title: string;
  references: WikiPageReference[];
  onPageSelect: (pageId: string) => void;
}) {
  return (
    <div className="space-y-1.5">
      <div className="flex items-center gap-1.5 text-[11px] font-medium uppercase tracking-[0.06em] text-ink-subtle">
        <BookOpenText className="h-3.5 w-3.5" strokeWidth={1.8} />
        {title}
      </div>
      <div className="flex flex-wrap gap-1.5">
        {references.slice(0, 6).map((reference) => (
          <button
            key={`${title}:${reference.pageId}`}
            type="button"
            className="inline-flex max-w-full items-center gap-1 rounded-full border border-line bg-canvas-raised px-2 py-1 text-[11px] font-medium text-ink-muted transition hover:border-accent/40 hover:bg-accent-soft hover:text-ink"
            title={reference.relativePath}
            onClick={() => onPageSelect(reference.pageId)}
          >
            <FileText className="h-3 w-3 shrink-0 text-accent" strokeWidth={1.8} />
            <span className="max-w-[13rem] truncate">{reference.title}</span>
          </button>
        ))}
      </div>
    </div>
  );
}

function PageChatBubble({ message, onSourceSelect }: { message: WikiPageChatMessage; onSourceSelect: (pageId: string) => void }) {
  if (message.kind === 'canvas') {
    return (
      <div className="max-w-full self-start rounded-xl border border-accent/35 bg-accent-soft px-3 py-2 text-xs text-ink">
        <div className="flex items-center gap-2">
          <WandSparkles className="h-3.5 w-3.5 shrink-0 text-accent" strokeWidth={2} />
          <span className="font-medium">Added to canvas</span>
          {message.summary ? (
            <span className="truncate text-ink-muted" title={message.summary}>— {message.summary}</span>
          ) : null}
        </div>
        <WikiAssistantSourceChips sources={message.sources ?? []} onSourceSelect={onSourceSelect} />
      </div>
    );
  }
  return (
    <div className={`max-w-full rounded-xl px-3 py-2 text-sm ${message.role === 'user' ? 'self-end bg-accent-soft text-ink' : 'self-start bg-canvas-raised text-ink'}`}>
      <p className="whitespace-pre-wrap leading-5">{message.text}</p>
      {message.role === 'assistant' ? <WikiAssistantSourceChips sources={message.sources ?? []} onSourceSelect={onSourceSelect} /> : null}
    </div>
  );
}

function WikiAssistantSourceChips({ sources, onSourceSelect }: { sources: WikiAssistantSource[]; onSourceSelect: (pageId: string) => void }) {
  if (sources.length === 0) return null;

  return (
    <div className="mt-2 flex flex-wrap gap-1.5" data-testid="wiki-assistant-sources">
      {sources.slice(0, 5).map((source) => (
        <button
          key={source.pageId}
          type="button"
          className="inline-flex max-w-full items-center gap-1 rounded-full border border-line bg-canvas/80 px-2 py-1 text-[11px] font-medium text-ink-muted transition hover:border-accent/40 hover:bg-canvas-raised hover:text-ink"
          title={`${source.relativePath}\n\n${source.snippet}`}
          onClick={() => onSourceSelect(source.pageId)}
        >
          <BookOpenText className="h-3 w-3 shrink-0 text-accent" strokeWidth={1.8} />
          <span className="max-w-[13rem] truncate">{source.title}</span>
        </button>
      ))}
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
  dropTargetFolderId,
  onFolderSelect,
  onPageSelect,
  onFolderRenameStart,
  onFolderDelete,
  onFolderRenameCancel,
  onFolderRenameSubmit,
  onFolderNameDraftChange,
  onFolderDragStart,
  onFolderDragOver,
  onFolderDragLeave,
  onFolderDrop,
}: {
  folder: WikiFolder | null;
  folders: WikiFolder[];
  pages: WikiPage[];
  selectedFolderId: string | null;
  selectedPageId: string | null;
  scope: WikiScope;
  renamingFolderId: string | null;
  folderNameDraft: string;
  dropTargetFolderId?: string | null;
  onFolderSelect: (folderId: string | null) => void;
  onPageSelect: (pageId: string) => void;
  onFolderRenameStart: (folder: WikiFolder) => void;
  onFolderDelete: (folder: WikiFolder) => void;
  onFolderRenameCancel: () => void;
  onFolderRenameSubmit: () => void;
  onFolderNameDraftChange: (name: string) => void;
  onFolderDragStart?: (event: React.DragEvent<HTMLDivElement>, folder: WikiFolder) => void;
  onFolderDragOver?: (event: React.DragEvent<HTMLDivElement>, folder: WikiFolder) => void;
  onFolderDragLeave?: (folder: WikiFolder) => void;
  onFolderDrop?: (event: React.DragEvent<HTMLDivElement>, folder: WikiFolder) => void;
}) {
  const isFolderSelected = folder ? selectedFolderId === folder.id && scope === 'folder' : false;
  const isRenaming = folder ? renamingFolderId === folder.id : false;
  const isDropTarget = folder ? dropTargetFolderId === folder.id : false;
  const childFolders = folder ? folders.filter((candidate) => candidate.parentFolderId === folder.id) : [];
  const sectionPages = folder ? pages.filter((page) => page.folderId === folder.id) : pages;
  const pageItems = (
    <ul className="space-y-1">
      {sectionPages.map((page) => (
        <li key={page.id}>
          <button
            type="button"
            onClick={() => onPageSelect(page.id)}
            className={`flex h-8 w-full items-center gap-2 rounded-lg px-2 text-left text-sm transition ${selectedPageId === page.id ? 'bg-accent-soft text-ink' : 'text-ink-muted hover:bg-canvas-raised/70 hover:text-ink'}`}
          >
            <FileText className="h-3.5 w-3.5 shrink-0" strokeWidth={1.8} />
            <span className="min-w-0 flex-1 truncate font-medium">{page.title}</span>
          </button>
        </li>
      ))}
    </ul>
  );

  if (!folder) {
    return <section>{pageItems}</section>;
  }

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
        <div
          draggable
          onDragStart={(event) => onFolderDragStart?.(event, folder)}
          onDragOver={(event) => onFolderDragOver?.(event, folder)}
          onDragLeave={() => onFolderDragLeave?.(folder)}
          onDrop={(event) => onFolderDrop?.(event, folder)}
          className={`flex h-8 w-full items-center gap-1 rounded-lg transition ${isDropTarget ? 'bg-accent/15 ring-1 ring-accent/40' : isFolderSelected ? 'bg-accent-soft text-ink' : 'text-ink-muted hover:bg-canvas-raised/70 hover:text-ink'}`}
        >
          <button
            type="button"
            onClick={() => onFolderSelect(folder.id)}
            className="flex min-w-0 flex-1 items-center gap-2 px-2 text-left text-sm font-medium"
          >
            <Folder className="h-3.5 w-3.5 shrink-0" strokeWidth={1.8} />
            <span className="truncate">{folder.name}</span>
          </button>
          {isFolderSelected ? (
            <div className="mr-1 flex shrink-0 items-center gap-1">
              <button
                type="button"
                className="flex h-6 w-6 items-center justify-center rounded-full text-ink-subtle transition hover:bg-canvas-raised hover:text-ink"
                title="Rename folder"
                aria-label="Rename folder"
                onClick={() => onFolderRenameStart(folder)}
              >
                <PencilLine className="h-3.5 w-3.5" strokeWidth={1.8} />
              </button>
              <button
                type="button"
                className="flex h-6 w-6 items-center justify-center rounded-full text-ink-subtle transition hover:bg-rose-500/10 hover:text-rose-600"
                title="Delete folder"
                aria-label="Delete folder"
                onClick={() => onFolderDelete(folder)}
              >
                <Trash2 className="h-3.5 w-3.5" strokeWidth={1.8} />
              </button>
            </div>
          ) : null}
        </div>
      )}
      {sectionPages.length > 0 ? <div className="mt-1 pl-3">{pageItems}</div> : null}
      {childFolders.length > 0 ? (
        <div className="mt-1 space-y-1 border-l border-line/70 pl-3">
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
              dropTargetFolderId={dropTargetFolderId}
              onFolderSelect={onFolderSelect}
              onPageSelect={onPageSelect}
              onFolderRenameStart={onFolderRenameStart}
              onFolderDelete={onFolderDelete}
              onFolderRenameCancel={onFolderRenameCancel}
              onFolderRenameSubmit={onFolderRenameSubmit}
              onFolderNameDraftChange={onFolderNameDraftChange}
              onFolderDragStart={onFolderDragStart}
              onFolderDragOver={onFolderDragOver}
              onFolderDragLeave={onFolderDragLeave}
              onFolderDrop={onFolderDrop}
            />
          ))}
        </div>
      ) : null}
    </section>
  );
}

// Selectable workspace-root row at the top of the page tree. Doubles as a
// drop target so dragging a folder onto the workspace pulls it back to the
// top level (parentFolderId = null).
function RootRow({
  name,
  selected,
  isDropTarget,
  onSelect,
  onDragOver,
  onDragLeave,
  onDrop,
}: {
  name: string;
  selected: boolean;
  isDropTarget: boolean;
  onSelect: () => void;
  onDragOver: (event: React.DragEvent<HTMLButtonElement>) => void;
  onDragLeave: () => void;
  onDrop: (event: React.DragEvent<HTMLButtonElement>) => void;
}) {
  return (
    <button
      type="button"
      onClick={onSelect}
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
      className={`flex h-8 w-full items-center gap-2 rounded-lg px-2 text-left text-sm font-medium transition ${isDropTarget ? 'bg-accent/15 ring-1 ring-accent/40 text-ink' : selected ? 'bg-accent-soft text-ink' : 'text-ink-muted hover:bg-canvas-raised/70 hover:text-ink'}`}
    >
      <Library className="h-3.5 w-3.5 shrink-0" strokeWidth={1.8} />
      <span className="min-w-0 flex-1 truncate uppercase tracking-[0.06em] text-[11px]">{name}</span>
    </button>
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

function RevisionItem({
  revision,
  active = false,
  previewing = false,
  disabled,
  onPreview,
  onRestore,
}: {
  revision: WikiRevision;
  active?: boolean;
  previewing?: boolean;
  disabled: boolean;
  onPreview: () => void;
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
          <p className="mt-0.5 text-[11px] text-ink-muted">
            {revision.source} · {formatStamp(revision.createdAt)}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          <button
            type="button"
            className={`inline-flex items-center gap-1 rounded-full border px-2.5 py-1 text-[11px] font-medium transition ${previewing ? 'border-accent/40 bg-canvas text-ink' : 'border-line text-ink-muted hover:bg-accent-soft hover:text-ink'}`}
            onClick={onPreview}
          >
            <Eye className="h-3 w-3" strokeWidth={1.8} />
            Preview
          </button>
          <button
            type="button"
            className="rounded-full border border-line px-2.5 py-1 text-[11px] font-medium text-ink-muted transition hover:bg-accent-soft hover:text-ink disabled:cursor-not-allowed disabled:opacity-40"
            disabled={disabled}
            onClick={onRestore}
          >
            Restore
          </button>
        </div>
      </div>
    </li>
  );
}

function RevisionPreview({
  revision,
  currentMarkdown,
  onClose,
}: {
  revision: WikiRevision;
  currentMarkdown: string;
  onClose: () => void;
}) {
  const revisionWords = countWords(revision.markdown);
  const wordDelta = revisionWords - countWords(currentMarkdown);
  return (
    <div className="rounded-xl border border-accent/30 bg-canvas-raised p-3">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <h3 className="truncate text-sm font-semibold text-ink">Version {revision.version}</h3>
          <p className="mt-0.5 text-[11px] text-ink-muted">
            {revision.source} · {formatStamp(revision.createdAt)} · {revisionWords} words · {wordDelta >= 0 ? '+' : ''}{wordDelta}
          </p>
        </div>
        <button type="button" className="wiki-icon-button h-7 w-7" aria-label="Close revision preview" onClick={onClose}>
          <X className="h-3.5 w-3.5" strokeWidth={1.8} />
        </button>
      </div>
      {revision.summary ? <p className="mt-2 text-xs text-ink-muted">{revision.summary}</p> : null}
      <pre className="mt-3 max-h-72 overflow-y-auto whitespace-pre-wrap rounded-lg border border-line bg-canvas p-3 text-[11px] leading-5 text-ink-muted">
        {revision.markdown || '(Empty revision)'}
      </pre>
    </div>
  );
}

function countWords(markdown: string): number {
  return markdown.trim().length === 0 ? 0 : markdown.trim().split(/\s+/).length;
}

interface WikiQuickSearchChip {
  kind: 'tag' | 'type' | 'marker';
  label: string;
  searchValue: string;
}

function extractWikiQuickSearchChips(markdown: string): WikiQuickSearchChip[] {
  const chips: WikiQuickSearchChip[] = [];
  const seen = new Set<string>();
  const add = (kind: WikiQuickSearchChip['kind'], label: string, searchValue = label) => {
    const normalizedLabel = label.trim();
    const normalizedSearch = searchValue.trim();
    if (!normalizedLabel || !normalizedSearch) return;
    const key = normalizedSearch.toLowerCase();
    if (seen.has(key)) return;
    seen.add(key);
    chips.push({ kind, label: normalizedLabel, searchValue: normalizedSearch });
  };

  for (const rawLine of markdown.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line) continue;

    const tags = extractInlineMetadataValue(line, 'tags?');
    if (tags) {
      for (const tag of parseTagLine(tags)) add('tag', tag, tag);
    }

    const type = extractInlineMetadataValue(line, 'type');
    if (type) {
      add('type', `type: ${type}`, `Type: ${type}`);
    }

    const marker = extractInlineMetadataValue(line, 'continuity marker');
    if (marker) {
      add('marker', marker, marker);
    }
  }

  for (const match of markdown.matchAll(/\b[A-Z0-9]+(?:-[A-Z0-9]+){2,}\b/g)) {
    add('marker', match[0], match[0]);
  }

  return chips.slice(0, 8);
}

function extractInlineMetadataValue(line: string, labelPattern: string): string | null {
  const match = new RegExp(`(?:^|\\s)${labelPattern}:\\s*(?<value>.+)`, 'i').exec(line);
  const raw = match?.groups?.value;
  if (!raw) return null;

  const nextField = /\s[A-Z][A-Za-z]*(?:\s+[A-Za-z]+){0,3}:\s/.exec(raw);
  const value = nextField && nextField.index > 0 ? raw.slice(0, nextField.index) : raw;
  return value.replace(/[.;]+$/g, '').trim() || null;
}

function parseTagLine(value: string): string[] {
  const explicit = Array.from(value.matchAll(/#[\p{L}\p{Nd}][\p{L}\p{Nd}_-]*/gu), (match) => match[0].toLowerCase());
  if (explicit.length > 0) return explicit;

  const parts = value.includes(',') ? value.split(',') : value.split(/\s+/);
  return parts
    .map((part) => part.trim().toLowerCase())
    .filter(Boolean)
    .map((part) => `#${part.replace(/[^\p{L}\p{Nd}_-]+/gu, '-').replace(/^-+|-+$/g, '')}`)
    .filter((part) => part.length > 1);
}

function truncateForPreview(text: string, max = 140): string {
  // Collapse whitespace so the inline preview reads as a single quoted line
  // even when the user highlighted across paragraphs.
  const flat = text.replace(/\s+/g, ' ').trim();
  return flat.length > max ? `${flat.slice(0, max - 1).trimEnd()}…` : flat;
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