import { create } from 'zustand';
import * as api from '../lib/wikiApi';
import type { WikiAssistantSource, WikiDownload, WikiPageDocument, WikiPageDraft, WikiPageGraph, WikiRevision, WikiRoot, WikiSearchResult, WikiSelectionRewriteDraft, WikiTrashItem, WikiTree } from '../lib/wikiApi';

export type WikiScope = 'root' | 'folder' | 'page';
export type WikiSearchScope = 'root' | 'all';

const LEGACY_UNTITLED_TITLE = 'Untitled Page';
const UNTITLED_CLEANUP_FLAG = 'sirthaddeus.wiki.cleanup.untitled-pages.v1';

// Derive a short placeholder title from a chat prompt so we can create a
// page row before the AI has produced markdown. The real title is refined
// from the AI output once it lands.
function deriveTitleFromPrompt(prompt: string): string {
  const cleaned = prompt.replace(/\s+/g, ' ').trim();
  if (!cleaned) return 'New page';
  const words = cleaned.split(' ').slice(0, 8).join(' ');
  return words.length > 60 ? words.slice(0, 60).trimEnd() : words;
}

// Derive a human-readable title from page content. Prefers the first
// markdown heading; falls back to the first non-empty line. Strips basic
// emphasis/link syntax and clamps to a sensible length.
export function deriveTitleFromMarkdown(markdown: string): string {
  if (!markdown) return '';
  for (const raw of markdown.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line) continue;
    const heading = /^#{1,6}\s+(.+)$/.exec(line);
    const source = (heading ? heading[1] : line).trim();
    const cleaned = source
      .replace(/!\[([^\]]*)\]\([^)]+\)/g, '$1')
      .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
      .replace(/[`*_~]/g, '')
      .trim();
    if (cleaned) return cleaned.length > 80 ? cleaned.slice(0, 80).trimEnd() : cleaned;
  }
  return '';
}

async function purgeLegacyUntitledPages(roots: WikiRoot[]): Promise<void> {
  if (typeof window === 'undefined') return;
  try {
    if (window.localStorage?.getItem(UNTITLED_CLEANUP_FLAG)) return;
  } catch {
    // localStorage unavailable (private mode, sandbox); skip cleanup.
    return;
  }
  for (const root of roots) {
    try {
      const tree = await api.getWikiTree(root.id);
      const offending = tree.pages.filter((p) => p.title === LEGACY_UNTITLED_TITLE);
      for (const page of offending) {
        try { await api.purgeWikiPage(page.id); } catch { /* ignore individual failures */ }
      }
    } catch {
      // tree fetch failed; skip this root
    }
  }
  try { window.localStorage?.setItem(UNTITLED_CLEANUP_FLAG, '1'); } catch { /* ignore */ }
}

async function loadPageSidecars(pageId: string): Promise<{ revisions: WikiRevision[]; pageGraph: WikiPageGraph }> {
  const [revisions, pageGraph] = await Promise.all([
    api.listWikiRevisions(pageId),
    api.getWikiPageGraph(pageId),
  ]);
  return { revisions, pageGraph };
}

export interface WikiPageChatMessage {
  id: string;
  role: 'user' | 'assistant';
  text: string;
  createdAt: string;
  // 'message' is a normal chat reply; 'canvas' is a compact pill the UI
  // renders to indicate the assistant wrote directly to the page (so we
  // don't echo the entire edited page back into the transcript).
  kind?: 'message' | 'canvas';
  summary?: string;
  sources?: WikiAssistantSource[];
}

interface WikiStoreState {
  roots: WikiRoot[];
  tree: WikiTree | null;
  page: WikiPageDocument | null;
  pageGraph: WikiPageGraph | null;
  revisions: WikiRevision[];
  selectedRootId: string | null;
  selectedFolderId: string | null;
  selectedPageId: string | null;
  // True when the user has clicked "New page" (or opened an empty workspace)
  // but no content has been persisted yet. Nothing is written to disk until
  // the user provides content or a title and triggers save.
  isDraftPage: boolean;
  draftTitle: string;
  draftFolderId: string | null;
  scope: WikiScope;
  searchScope: WikiSearchScope;
  search: string;
  searchResults: WikiSearchResult[];
  trashItems: WikiTrashItem[];
  draft: string;
  pageChatMessages: WikiPageChatMessage[];
  pageChatDraft: WikiPageDraft | null;
  selectedText: string;
  selectionRewriteDraft: WikiSelectionRewriteDraft | null;
  dirty: boolean;
  // The first markdown the Tiptap editor emits after a (re)mount establishes
  // the baseline used for dirty detection. This avoids false "Unsaved" flags
  // caused by lossless markdown round-trip differences (whitespace, list
  // bullet style, paragraph collapsing) between the persisted markdown and
  // what Tiptap+Turndown produce. Tracked per page-version key so navigating
  // to a new page or saving (which bumps version) forces a fresh baseline.
  editorBaseline: string;
  editorBaselineKey: string | null;
  loading: boolean;
  saving: boolean;
  searching: boolean;
  trashLoading: boolean;
  pageAssistantBusy: boolean;
  error: string | null;

  loadRoots: () => Promise<void>;
  selectRoot: (rootId: string) => Promise<void>;
  selectFolder: (folderId: string | null) => void;
  selectPage: (pageId: string) => Promise<void>;
  createRoot: () => Promise<void>;
  renameRoot: (rootId: string, name: string) => Promise<void>;
  deleteRoot: (rootId: string) => Promise<void>;
  exportRoot: (rootId: string) => Promise<WikiDownload | null>;
  createFolder: () => Promise<void>;
  renameFolder: (folderId: string, name: string) => Promise<void>;
  moveFolder: (folderId: string, parentFolderId: string | null) => Promise<void>;
  deleteFolder: (folderId: string) => Promise<void>;
  loadTrash: () => Promise<void>;
  restoreTrashItem: (item: WikiTrashItem) => Promise<void>;
  purgeTrashItem: (item: WikiTrashItem) => Promise<void>;
  createPage: () => Promise<void>;
  savePage: () => Promise<void>;
  renamePage: (title: string) => Promise<void>;
  movePage: (folderId: string | null) => Promise<void>;
  deletePage: () => Promise<void>;
  discardDraft: () => void;
  setDraftTitle: (title: string) => void;
  restoreRevision: (revisionId: string) => Promise<void>;
  undoLatestAiEdit: () => Promise<void>;
  askPage: (prompt: string) => Promise<void>;
  draftPage: (instruction: string) => Promise<void>;
  applyPageDraft: () => Promise<void>;
  clearPageDraft: () => void;
  rewriteSelection: (instruction: string) => Promise<void>;
  applySelectionRewrite: () => Promise<void>;
  clearSelectionRewrite: () => void;
  setDraft: (markdown: string) => void;
  setSelectedText: (text: string) => void;
  setSearch: (search: string) => void;
  setSearchScope: (scope: WikiSearchScope) => void;
  setScope: (scope: WikiScope) => void;
}

export const useWikiStore = create<WikiStoreState>((set, get) => ({
  roots: [],
  tree: null,
  page: null,
  pageGraph: null,
  revisions: [],
  selectedRootId: null,
  selectedFolderId: null,
  selectedPageId: null,
  isDraftPage: false,
  draftTitle: '',
  draftFolderId: null,
  scope: 'root',
  searchScope: 'root',
  search: '',
  searchResults: [],
  trashItems: [],
  draft: '',
  pageChatMessages: [],
  pageChatDraft: null,
  selectedText: '',
  selectionRewriteDraft: null,
  dirty: false,
  editorBaseline: '',
  editorBaselineKey: null,
  loading: false,
  saving: false,
  searching: false,
  trashLoading: false,
  pageAssistantBusy: false,
  error: null,

  loadRoots: async () => {
    set({ loading: true, error: null });
    try {
      let roots = await api.listWikiRoots();
      // One-shot cleanup: delete the historical "Untitled Page" rows that
      // accumulated from earlier auto-create paths. Idempotent, guarded by a
      // localStorage flag so it never runs twice.
      await purgeLegacyUntitledPages(roots);
      set({ roots, loading: false });
      const selectedRootId = get().selectedRootId;
      const selectedRootStillExists = selectedRootId ? roots.some((root) => root.id === selectedRootId) : false;
      if (selectedRootStillExists && selectedRootId) {
        await get().selectRoot(selectedRootId);
      } else if (roots.length > 0) {
        await get().selectRoot(roots[0].id);
      } else {
        const createdRoot = await api.createWikiRoot({ name: 'My Wiki' });
        roots = [createdRoot];
        set({ roots });
        await get().selectRoot(createdRoot.id);
      }
    } catch (error) {
      set({ error: (error as Error).message, loading: false });
    }
  },

  selectRoot: async (rootId: string) => {
    if (get().dirty) {
      set({ error: 'Save or discard changes before switching workspaces.' });
      return;
    }
    set({ loading: true, error: null, selectedRootId: rootId, selectedFolderId: null, selectedPageId: null, page: null, pageGraph: null, revisions: [], search: '', searchResults: [], trashItems: [], searching: false, trashLoading: false, draft: '', pageChatMessages: [], pageChatDraft: null, selectedText: '', selectionRewriteDraft: null, dirty: false, isDraftPage: false, draftTitle: '', draftFolderId: null, scope: 'root' });
    try {
      const tree = await api.getWikiTree(rootId);
      if (tree.pages.length > 0) {
        set({ tree });
        for (const candidate of tree.pages) {
          await get().selectPage(candidate.id);
          if (get().page?.page.id === candidate.id) return;
        }
      }

      // Empty workspace: open an in-memory draft so the editor is immediately
      // usable, but do NOT persist anything until the user provides content.
      set({
        tree,
        page: null,
        pageGraph: null,
        revisions: [],
        selectedFolderId: null,
        selectedPageId: null,
        draft: '',
        pageChatMessages: [],
        pageChatDraft: null,
        selectedText: '',
        selectionRewriteDraft: null,
        dirty: false,
        isDraftPage: true,
        draftTitle: '',
        draftFolderId: null,
        scope: 'page',
        loading: false,
      });
    } catch (error) {
      set({ error: (error as Error).message, loading: false });
    }
  },

  selectFolder: (folderId: string | null) => {
    set({ selectedFolderId: folderId, scope: folderId ? 'folder' : 'root' });
  },

  selectPage: async (pageId: string) => {
    if (get().dirty && pageId !== get().selectedPageId) {
      set({ error: 'Save or discard changes before switching pages.' });
      return;
    }
    set({ loading: true, error: null, selectedPageId: pageId, isDraftPage: false, draftTitle: '', draftFolderId: null });
    try {
      const page = await api.getWikiPage(pageId);
      const { revisions, pageGraph } = await loadPageSidecars(pageId);
      const tree = get().tree?.root.id === page.page.rootId ? get().tree : await api.getWikiTree(page.page.rootId);
      set({
        tree,
        page,
        pageGraph,
        revisions,
        selectedPageId: page.page.id,
        selectedFolderId: page.page.folderId,
        selectedRootId: page.page.rootId,
        draft: page.markdown,
        pageChatMessages: [],
        pageChatDraft: null,
        selectedText: '',
        selectionRewriteDraft: null,
        dirty: false,
        scope: 'page',
        loading: false,
      });
    } catch (error) {
      if (isRuntimeNotFound(error)) {
        // Stale/persisted id no longer exists. Clear selection and let the
        // empty state render — do NOT silently create a replacement page.
        set({
          page: null,
          pageGraph: null,
          revisions: [],
          selectedPageId: null,
          draft: '',
          dirty: false,
          loading: false,
          error: null,
        });
        return;
      }
      set({ error: (error as Error).message, loading: false });
    }
  },

  createRoot: async () => {
    if (get().dirty) {
      set({ error: 'Save or discard changes before creating a workspace.' });
      return;
    }
    set({ saving: true, error: null });
    try {
      const created = await api.createWikiRoot({ name: `Workspace ${new Date().toLocaleDateString()}` });
      set((state) => ({ roots: [created, ...state.roots] }));
      await get().selectRoot(created.id);
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  renameRoot: async (rootId: string, name: string) => {
    const trimmed = name.trim();
    if (!rootId || !trimmed) return;
    const currentRoot = get().roots.find((root) => root.id === rootId) ?? null;
    if (currentRoot && currentRoot.name === trimmed) return;
    if (get().dirty) {
      set({ error: 'Save or discard changes before renaming this workspace.' });
      return;
    }

    set({ saving: true, error: null });
    try {
      const renamed = await api.updateWikiRoot(rootId, { name: trimmed });
      set((state) => ({
        roots: state.roots.map((root) => (root.id === renamed.id ? renamed : root)),
        tree: state.tree?.root.id === renamed.id ? { ...state.tree, root: renamed } : state.tree,
      }));
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  deleteRoot: async (rootId: string) => {
    if (!rootId) return;
    if (get().dirty) {
      set({ error: 'Save or discard changes before removing this workspace.' });
      return;
    }

    set({ saving: true, error: null });
    try {
      const wasSelected = get().selectedRootId === rootId;
      await api.deleteWikiRoot(rootId);
      const roots = await api.listWikiRoots();
      set({ roots });

      if (!wasSelected) return;

      if (roots.length > 0) {
        await get().selectRoot(roots[0].id);
        return;
      }

      set({ tree: null, page: null, pageGraph: null, revisions: [], selectedRootId: null, selectedFolderId: null, selectedPageId: null, scope: 'root', searchScope: 'root', search: '', searchResults: [], trashItems: [], draft: '', pageChatMessages: [], pageChatDraft: null, selectedText: '', selectionRewriteDraft: null, dirty: false });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  exportRoot: async (rootId: string) => {
    if (!rootId) return null;
    if (get().dirty) {
      set({ error: 'Save or discard changes before exporting this workspace.' });
      return null;
    }

    set({ saving: true, error: null });
    try {
      return await api.exportWikiRoot(rootId);
    } catch (error) {
      set({ error: (error as Error).message });
      return null;
    } finally {
      set({ saving: false });
    }
  },

  createFolder: async () => {
    if (get().dirty) {
      set({ error: 'Save or discard changes before creating a folder.' });
      return;
    }
    const rootId = get().selectedRootId;
    if (!rootId) return;
    const parentFolderId = get().scope === 'folder' ? get().selectedFolderId : null;
    set({ saving: true, error: null });
    try {
      const created = await api.createWikiFolder(rootId, { name: 'New Folder', parentFolderId });
      const tree = await api.getWikiTree(rootId);
      set({ tree, selectedFolderId: created.id, scope: 'folder' });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  renameFolder: async (folderId: string, name: string) => {
    const rootId = get().selectedRootId;
    const trimmed = name.trim();
    if (!rootId || !folderId || !trimmed) return;
    const currentFolder = get().tree?.folders.find((folder) => folder.id === folderId) ?? null;
    if (currentFolder && currentFolder.name === trimmed) return;
    if (get().dirty) {
      set({ error: 'Save or discard changes before renaming this folder.' });
      return;
    }

    set({ saving: true, error: null });
    try {
      const renamed = await api.updateWikiFolder(rootId, folderId, { name: trimmed });
      const tree = await api.getWikiTree(rootId);
      const currentPage = get().page;
      const refreshedPage = currentPage ? await api.getWikiPage(currentPage.page.id) : null;
      set({
        tree,
        page: refreshedPage,
        draft: refreshedPage?.markdown ?? get().draft,
        selectedFolderId: renamed.id,
        selectedText: '',
        selectionRewriteDraft: null,
        dirty: false,
      });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  moveFolder: async (folderId: string, parentFolderId: string | null) => {
    const rootId = get().selectedRootId;
    const targetParentFolderId = parentFolderId || null;
    if (!rootId || !folderId) return;
    const currentFolder = get().tree?.folders.find((folder) => folder.id === folderId) ?? null;
    if (!currentFolder || currentFolder.parentFolderId === targetParentFolderId) return;
    if (get().dirty) {
      set({ error: 'Save or discard changes before moving this folder.' });
      return;
    }

    set({ saving: true, error: null });
    try {
      const moved = await api.moveWikiFolder(rootId, folderId, { parentFolderId: targetParentFolderId });
      const tree = await api.getWikiTree(rootId);
      const currentPage = get().page;
      const refreshedPage = currentPage ? await api.getWikiPage(currentPage.page.id) : null;
      set({
        tree,
        page: refreshedPage,
        draft: refreshedPage?.markdown ?? get().draft,
        selectedFolderId: moved.id,
        selectedText: '',
        selectionRewriteDraft: null,
        dirty: false,
        scope: 'folder',
      });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  deleteFolder: async (folderId: string) => {
    const rootId = get().selectedRootId;
    if (!rootId || !folderId) return;
    const folders = get().tree?.folders ?? [];
    const currentFolder = folders.find((folder) => folder.id === folderId) ?? null;
    if (!currentFolder) return;
    if (get().dirty) {
      set({ error: 'Save or discard changes before deleting this folder.' });
      return;
    }

    set({ saving: true, error: null });
    try {
      await api.deleteWikiFolder(rootId, folderId);
      const tree = await api.getWikiTree(rootId);
      set({
        tree,
        page: null,
        pageGraph: null,
        revisions: [],
        selectedPageId: null,
        selectedFolderId: currentFolder.parentFolderId,
        draft: '',
        pageChatMessages: [],
        pageChatDraft: null,
        selectedText: '',
        selectionRewriteDraft: null,
        dirty: false,
        scope: currentFolder.parentFolderId ? 'folder' : 'root',
      });
      await get().loadTrash();
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  loadTrash: async () => {
    const rootId = get().selectedRootId;
    if (!rootId) {
      set({ trashItems: [], trashLoading: false });
      return;
    }

    set({ trashLoading: true, error: null });
    try {
      const trashItems = await api.listWikiTrash(rootId);
      if (get().selectedRootId === rootId) {
        set({ trashItems, trashLoading: false });
      }
    } catch (error) {
      if (get().selectedRootId === rootId) {
        set({ error: (error as Error).message, trashItems: [], trashLoading: false });
      }
    }
  },

  restoreTrashItem: async (item: WikiTrashItem) => {
    if (!item || get().dirty) {
      if (get().dirty) set({ error: 'Save or discard changes before restoring from trash.' });
      return;
    }

    set({ saving: true, error: null });
    try {
      if (item.type === 'page') {
        const restored = await api.restoreWikiPage(item.id);
        const tree = await api.getWikiTree(restored.page.rootId);
        const { revisions, pageGraph } = await loadPageSidecars(restored.page.id);
        const trashItems = await api.listWikiTrash(restored.page.rootId);
        set({
          tree,
          page: restored,
          pageGraph,
          revisions,
          selectedRootId: restored.page.rootId,
          selectedFolderId: restored.page.folderId,
          selectedPageId: restored.page.id,
          draft: restored.markdown,
          pageChatMessages: [],
          pageChatDraft: null,
          selectedText: '',
          selectionRewriteDraft: null,
          dirty: false,
          isDraftPage: false,
          draftTitle: '',
          draftFolderId: null,
          scope: 'page',
          trashItems,
        });
        return;
      }

      await api.restoreWikiFolder(item.rootId, item.id);
      const tree = await api.getWikiTree(item.rootId);
      const trashItems = await api.listWikiTrash(item.rootId);
      set({ tree, selectedFolderId: item.id, selectedPageId: null, page: null, pageGraph: null, revisions: [], draft: '', selectedText: '', selectionRewriteDraft: null, dirty: false, scope: 'folder', trashItems });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  purgeTrashItem: async (item: WikiTrashItem) => {
    if (!item || get().dirty) {
      if (get().dirty) set({ error: 'Save or discard changes before deleting from trash.' });
      return;
    }

    set({ saving: true, error: null });
    try {
      if (item.type === 'page') {
        await api.purgeWikiPage(item.id);
      } else {
        await api.purgeWikiFolder(item.rootId, item.id);
      }
      const tree = await api.getWikiTree(item.rootId);
      const trashItems = await api.listWikiTrash(item.rootId);
      set({ tree, trashItems });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  createPage: async () => {
    if (get().dirty) {
      set({ error: 'Save or discard changes before creating a page.' });
      return;
    }
    const rootId = get().selectedRootId;
    if (!rootId) return;
    // Open an in-memory draft. Nothing hits the backend until the user adds
    // a title or content and triggers save (manually or via Ctrl+S).
    set({
      page: null,
      pageGraph: null,
      revisions: [],
      selectedPageId: null,
      draft: '',
      pageChatMessages: [],
      pageChatDraft: null,
      selectedText: '',
      selectionRewriteDraft: null,
      dirty: false,
      isDraftPage: true,
      draftTitle: '',
      draftFolderId: get().selectedFolderId,
      scope: 'page',
      error: null,
    });
  },

  savePage: async () => {
    // Draft path: persist the in-memory draft as a brand-new page, deriving
    // a title from the user-supplied title or the first heading/line of the
    // markdown. Refuses to save when both are empty.
    if (get().isDraftPage) {
      const rootId = get().selectedRootId;
      if (!rootId) return;
      const draft = get().draft;
      const explicitTitle = get().draftTitle.trim();
      const derivedTitle = deriveTitleFromMarkdown(draft);
      const title = explicitTitle || derivedTitle;
      if (!title && !draft.trim()) {
        set({ error: 'Add a title or some content before saving.' });
        return;
      }
      const finalTitle = title || 'Untitled';
      set({ saving: true, error: null });
      try {
        const created = await api.createWikiPage(rootId, {
          title: finalTitle,
          folderId: get().draftFolderId,
          markdown: draft,
        });
        const tree = await api.getWikiTree(rootId);
        const { revisions, pageGraph } = await loadPageSidecars(created.page.id);
        set({
          tree,
          page: created,
          pageGraph,
          revisions,
          selectedPageId: created.page.id,
          selectedFolderId: created.page.folderId,
          draft: created.markdown,
          pageChatMessages: [],
          pageChatDraft: null,
          selectedText: '',
          selectionRewriteDraft: null,
          dirty: false,
          isDraftPage: false,
          draftTitle: '',
          draftFolderId: null,
          scope: 'page',
        });
      } catch (error) {
        set({ error: (error as Error).message });
      } finally {
        set({ saving: false });
      }
      return;
    }

    const current = get().page;
    if (!current || !get().dirty) return;
    set({ saving: true, error: null });
    try {
      const saved = await api.updateWikiPage(current.page.id, {
        markdown: get().draft,
        expectedVersion: current.page.version,
        source: 'user',
        summary: 'Manual save',
      });
      const tree = await api.getWikiTree(saved.page.rootId);
      const { revisions, pageGraph } = await loadPageSidecars(saved.page.id);
      set({ page: saved, pageGraph, tree, revisions, draft: saved.markdown, selectedText: '', selectionRewriteDraft: null, dirty: false });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  renamePage: async (title: string) => {
    const current = get().page;
    const trimmed = title.trim();
    if (!current || !trimmed || trimmed === current.page.title) return;
    if (get().dirty) {
      set({ error: 'Save or discard changes before renaming this page.' });
      return;
    }
    set({ saving: true, error: null });
    try {
      const renamed = await api.updateWikiPage(current.page.id, {
        title: trimmed,
        expectedVersion: current.page.version,
        source: 'user',
        summary: 'Renamed page',
      });
      const tree = await api.getWikiTree(renamed.page.rootId);
      const { revisions, pageGraph } = await loadPageSidecars(renamed.page.id);
      set({ page: renamed, pageGraph, tree, revisions, draft: renamed.markdown, selectedPageId: renamed.page.id, selectedText: '', selectionRewriteDraft: null, dirty: false });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  movePage: async (folderId: string | null) => {
    const current = get().page;
    if (!current) return;
    const targetFolderId = folderId || null;
    if (current.page.folderId === targetFolderId) return;
    if (get().dirty) {
      set({ error: 'Save or discard changes before moving this page.' });
      return;
    }

    set({ saving: true, error: null });
    try {
      const moved = await api.moveWikiPage(current.page.id, {
        folderId: targetFolderId,
        expectedVersion: current.page.version,
      });
      const tree = await api.getWikiTree(moved.page.rootId);
      const { revisions, pageGraph } = await loadPageSidecars(moved.page.id);
      set({
        page: moved,
        pageGraph,
        tree,
        revisions,
        draft: moved.markdown,
        selectedPageId: moved.page.id,
        selectedFolderId: moved.page.folderId,
        selectedText: '',
        selectionRewriteDraft: null,
        dirty: false,
        scope: 'page',
      });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  deletePage: async () => {
    const current = get().page;
    if (!current) return;
    if (get().dirty) {
      set({ error: 'Save or discard changes before deleting this page.' });
      return;
    }

    set({ saving: true, error: null });
    try {
      await api.deleteWikiPage(current.page.id);
      const tree = await api.getWikiTree(current.page.rootId);
      set({
        tree,
        page: null,
        pageGraph: null,
        revisions: [],
        selectedPageId: null,
        selectedFolderId: current.page.folderId,
        draft: '',
        pageChatMessages: [],
        pageChatDraft: null,
        selectedText: '',
        selectionRewriteDraft: null,
        dirty: false,
        scope: current.page.folderId ? 'folder' : 'root',
      });
      await get().loadTrash();
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  discardDraft: () => {
    if (get().isDraftPage) {
      // Drop the in-memory draft entirely. Nothing was written, so there is
      // no rollback to perform.
      set({
        draft: '',
        draftTitle: '',
        draftFolderId: null,
        isDraftPage: false,
        selectedText: '',
        selectionRewriteDraft: null,
        dirty: false,
        error: null,
        scope: get().selectedFolderId ? 'folder' : 'root',
      });
      return;
    }
    const current = get().page;
    if (!current) return;
    set({ draft: current.markdown, selectedText: '', selectionRewriteDraft: null, dirty: false, error: null });
  },

  setDraftTitle: (title: string) => {
    if (!get().isDraftPage) return;
    set((state) => ({
      draftTitle: title,
      dirty: title.trim().length > 0 || state.draft.trim().length > 0,
    }));
  },

  restoreRevision: async (revisionId: string) => {
    const current = get().page;
    if (!current) return;
    if (get().dirty) {
      set({ error: 'Save or discard changes before restoring a revision.' });
      return;
    }
    set({ saving: true, error: null });
    try {
      const restored = await api.restoreWikiRevision(
        current.page.id,
        revisionId,
        current.page.version,
      );
      const tree = await api.getWikiTree(restored.page.rootId);
      const { revisions, pageGraph } = await loadPageSidecars(restored.page.id);
      set({
        page: restored,
        pageGraph,
        tree,
        revisions,
        selectedPageId: restored.page.id,
        selectedFolderId: restored.page.folderId,
        draft: restored.markdown,
        pageChatDraft: null,
        selectedText: '',
        selectionRewriteDraft: null,
        dirty: false,
        scope: 'page',
      });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  undoLatestAiEdit: async () => {
    const latest = get().revisions[0];
    const previous = get().revisions[1];
    if (!latest || !previous || latest.source !== 'ai') return;
    await get().restoreRevision(previous.id);
  },

  askPage: async (prompt: string) => {
    const current = get().page;
    const trimmed = prompt.trim();
    if (!current || !trimmed) return;
    const userMessage: WikiPageChatMessage = {
      id: newLocalMessageId('user'),
      role: 'user',
      text: trimmed,
      createdAt: new Date().toISOString(),
    };
    set((state) => ({
      pageAssistantBusy: true,
      error: null,
      pageChatMessages: [...state.pageChatMessages, userMessage],
    }));
    try {
      const reply = await api.askWikiPage(current.page.id, { prompt: trimmed, scope: get().scope });
      set((state) => ({
        pageChatMessages: [
          ...state.pageChatMessages,
          { id: reply.messageId, role: 'assistant', text: reply.answer, createdAt: reply.createdAt, sources: reply.sources ?? [] },
        ],
      }));
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ pageAssistantBusy: false });
    }
  },

  draftPage: async (instruction: string) => {
    const trimmed = instruction.trim();
    if (!trimmed) return;
    let current = get().page;
    // Draft-mode bootstrap: there is no saved page yet, so create one with a
    // placeholder title derived from the prompt. If the AI fails or returns
    // empty markdown below, we delete the page so nothing empty is persisted.
    let bootstrappedPageId: string | null = null;
    if (!current && get().isDraftPage) {
      const rootId = get().selectedRootId;
      if (!rootId) return;
      const placeholder = get().draftTitle.trim() || deriveTitleFromPrompt(trimmed);
      try {
        const created = await api.createWikiPage(rootId, {
          title: placeholder,
          folderId: get().draftFolderId,
          markdown: '',
        });
        bootstrappedPageId = created.page.id;
        set({
          page: created,
          pageGraph: { links: [], backlinks: [], tags: [] },
          revisions: [],
          selectedPageId: created.page.id,
          selectedFolderId: created.page.folderId,
          draft: created.markdown,
          isDraftPage: false,
          draftTitle: '',
          draftFolderId: null,
          scope: 'page',
          dirty: false,
        });
        current = created;
      } catch (error) {
        set({ error: (error as Error).message });
        return;
      }
    }
    if (!current) return;
    // Auto-flush any pending user edits so the AI works against the latest content.
    if (get().dirty) {
      await get().savePage();
      if (get().error) return;
      current = get().page;
      if (!current) return;
    }

    const userMessage: WikiPageChatMessage = {
      id: newLocalMessageId('user'),
      role: 'user',
      text: trimmed,
      createdAt: new Date().toISOString(),
    };
    set((state) => ({
      pageAssistantBusy: true,
      saving: true,
      error: null,
      pageChatDraft: null,
      pageChatMessages: [...state.pageChatMessages, userMessage],
    }));
    try {
      const aiDraft = await api.draftWikiPage(current.page.id, { instruction: trimmed, scope: get().scope });
      // Empty AI output on a brand-new page would leave a blank row behind.
      // Roll the bootstrap back so the user sees nothing was saved.
      if (bootstrappedPageId && !aiDraft.markdown.trim()) {
        try { await api.purgeWikiPage(bootstrappedPageId); } catch { /* best-effort */ }
        const tree = await api.getWikiTree(current.page.rootId);
        set((state) => ({
          tree,
          page: null,
          pageGraph: null,
          revisions: [],
          selectedPageId: null,
          draft: '',
          isDraftPage: true,
          draftTitle: '',
          draftFolderId: get().selectedFolderId,
          scope: 'page',
          pageChatMessages: [
            ...state.pageChatMessages,
            {
              id: aiDraft.messageId,
              role: 'assistant',
              text: aiDraft.assistantText || 'No content was generated, so nothing was saved.',
              createdAt: aiDraft.createdAt,
              sources: aiDraft.sources ?? [],
            },
          ],
        }));
        return;
      }
      const saved = await api.updateWikiPage(current.page.id, {
        markdown: aiDraft.markdown,
        expectedVersion: current.page.version,
        source: 'ai',
        summary: aiDraft.summary,
      });
      // After the AI drops content into a freshly-bootstrapped page, prefer a
      // title derived from that content over the placeholder we created with.
      let finalSaved = saved;
      if (bootstrappedPageId) {
        const derived = deriveTitleFromMarkdown(saved.markdown);
        if (derived && derived !== saved.page.title) {
          try {
            finalSaved = await api.updateWikiPage(saved.page.id, {
              title: derived,
              expectedVersion: saved.page.version,
              source: 'ai',
              summary: 'Derived title from generated content',
            });
          } catch { /* keep placeholder title on rename failure */ }
        }
      }
      const tree = await api.getWikiTree(finalSaved.page.rootId);
      const { revisions, pageGraph } = await loadPageSidecars(finalSaved.page.id);
      set((state) => ({
        page: finalSaved,
        pageGraph,
        tree,
        revisions,
        draft: finalSaved.markdown,
        selectedPageId: finalSaved.page.id,
        selectedFolderId: finalSaved.page.folderId,
        pageChatDraft: null,
        selectedText: '',
        selectionRewriteDraft: null,
        dirty: false,
        scope: 'page',
        pageChatMessages: [
          ...state.pageChatMessages,
          {
            id: aiDraft.messageId,
            role: 'assistant',
            text: aiDraft.summary || aiDraft.assistantText || 'Updated the page.',
            summary: aiDraft.summary || aiDraft.assistantText || 'Updated the page.',
            kind: 'canvas',
            createdAt: aiDraft.createdAt,
            sources: aiDraft.sources ?? [],
          },
        ],
      }));
    } catch (error) {
      // If the AI call failed during bootstrap, delete the placeholder page so
      // we don't leave a blank row behind.
      if (bootstrappedPageId) {
        try { await api.purgeWikiPage(bootstrappedPageId); } catch { /* best-effort */ }
        const rootId = get().selectedRootId;
        const tree = rootId ? await api.getWikiTree(rootId).catch(() => get().tree) : get().tree;
        set({
          tree,
          page: null,
          pageGraph: null,
          revisions: [],
          selectedPageId: null,
          draft: '',
          isDraftPage: true,
          draftTitle: '',
          draftFolderId: get().selectedFolderId,
          scope: 'page',
        });
      }
      set({ error: (error as Error).message });
    } finally {
      set({ pageAssistantBusy: false, saving: false });
    }
  },

  applyPageDraft: async () => {
    const current = get().page;
    const pendingDraft = get().pageChatDraft;
    if (!current || !pendingDraft) return;
    if (get().dirty) {
      set({ error: 'Save or discard changes before applying an AI draft.' });
      return;
    }
    set({ saving: true, error: null });
    try {
      const saved = await api.updateWikiPage(current.page.id, {
        markdown: pendingDraft.markdown,
        expectedVersion: current.page.version,
        source: 'ai',
        summary: pendingDraft.summary,
      });
      const tree = await api.getWikiTree(saved.page.rootId);
      const { revisions, pageGraph } = await loadPageSidecars(saved.page.id);
      set({ page: saved, pageGraph, tree, revisions, draft: saved.markdown, selectedText: '', selectionRewriteDraft: null, dirty: false, pageChatDraft: null });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  clearPageDraft: () => set({ pageChatDraft: null }),

  rewriteSelection: async (instruction: string) => {
    const trimmed = instruction.trim();
    const selectedText = get().selectedText.trim();
    if (!trimmed || !selectedText) return;
    let current = get().page;
    if (!current) return;
    // Auto-flush any pending user edits so the selection still resolves against the saved page.
    if (get().dirty) {
      await get().savePage();
      if (get().error) return;
      current = get().page;
      if (!current) return;
    }

    const userMessage: WikiPageChatMessage = {
      id: newLocalMessageId('user'),
      role: 'user',
      text: `Rewrite selection: ${trimmed}`,
      createdAt: new Date().toISOString(),
    };
    set((state) => ({
      pageAssistantBusy: true,
      saving: true,
      error: null,
      selectionRewriteDraft: null,
      pageChatMessages: [...state.pageChatMessages, userMessage],
    }));
    try {
      const rewriteDraft = await api.rewriteWikiSelection(current.page.id, {
        selectedText,
        instruction: trimmed,
        expectedVersion: current.page.version,
        scope: get().scope,
      });
      const saved = await api.updateWikiPage(current.page.id, {
        markdown: rewriteDraft.markdown,
        expectedVersion: current.page.version,
        source: 'ai',
        summary: rewriteDraft.summary,
      });
      const tree = await api.getWikiTree(saved.page.rootId);
      const { revisions, pageGraph } = await loadPageSidecars(saved.page.id);
      set((state) => ({
        page: saved,
        pageGraph,
        tree,
        revisions,
        draft: saved.markdown,
        selectedPageId: saved.page.id,
        selectedFolderId: saved.page.folderId,
        selectedText: '',
        selectionRewriteDraft: null,
        pageChatDraft: null,
        dirty: false,
        pageChatMessages: [
          ...state.pageChatMessages,
          {
            id: rewriteDraft.messageId,
            role: 'assistant',
            text: rewriteDraft.summary || rewriteDraft.assistantText || 'Rewrote the selection.',
            summary: rewriteDraft.summary || rewriteDraft.assistantText || 'Rewrote the selection.',
            kind: 'canvas',
            createdAt: rewriteDraft.createdAt,
            sources: rewriteDraft.sources ?? [],
          },
        ],
      }));
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ pageAssistantBusy: false, saving: false });
    }
  },

  applySelectionRewrite: async () => {
    const current = get().page;
    const pendingDraft = get().selectionRewriteDraft;
    if (!current || !pendingDraft) return;
    if (get().dirty) {
      set({ error: 'Save or discard changes before applying an AI rewrite.' });
      return;
    }
    set({ saving: true, error: null });
    try {
      const saved = await api.updateWikiPage(current.page.id, {
        markdown: pendingDraft.markdown,
        expectedVersion: current.page.version,
        source: 'ai',
        summary: pendingDraft.summary,
      });
      const tree = await api.getWikiTree(saved.page.rootId);
      const { revisions, pageGraph } = await loadPageSidecars(saved.page.id);
      set({ page: saved, pageGraph, tree, revisions, draft: saved.markdown, selectedText: '', selectionRewriteDraft: null, dirty: false });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  clearSelectionRewrite: () => set({ selectionRewriteDraft: null }),

  setDraft: (markdown: string) => set((state) => {
    const normalize = (value: string) => value.replace(/\s+$/g, '');
    if (state.isDraftPage) {
      // For a draft, dirty is true as soon as the user adds any content or
      // a title. Anything blank stays clean so navigation does not get
      // blocked by an empty draft.
      const hasContent = normalize(markdown).length > 0 || state.draftTitle.trim().length > 0;
      return {
        draft: markdown,
        dirty: hasContent,
        editorBaseline: '',
        editorBaselineKey: 'draft',
        selectionRewriteDraft: null,
      };
    }
    // Use Tiptap's own first-emit as the dirty baseline so lossless
    // round-trip differences (whitespace / list spacing / collapsed inline
    // labels) don't falsely flag a freshly loaded page as Unsaved.
    const currentKey = state.page ? `${state.page.page.id}:${state.page.page.version}` : null;
    if (currentKey === null) {
      return { draft: markdown, selectionRewriteDraft: null };
    }
    if (state.editorBaselineKey !== currentKey) {
      return {
        draft: markdown,
        editorBaseline: markdown,
        editorBaselineKey: currentKey,
        dirty: false,
        selectionRewriteDraft: null,
      };
    }
    return {
      draft: markdown,
      dirty: normalize(markdown) !== normalize(state.editorBaseline),
      selectionRewriteDraft: null,
    };
  }),
  setSelectedText: (text: string) => set({ selectedText: text }),
  setSearch: (search: string) => {
    set({ search });
    const query = search.trim();
    const searchScope = get().searchScope;
    const rootId = searchScope === 'all' ? null : get().selectedRootId;
    if (!query || (searchScope === 'root' && !rootId)) {
      set({ searchResults: [], searching: false });
      return;
    }

    set({ searching: true, error: null });
    void api.searchWiki(rootId, query)
      .then((results) => {
        const current = get();
        const rootStillMatches = searchScope === 'all' || current.selectedRootId === rootId;
        if (current.search.trim() === query && current.searchScope === searchScope && rootStillMatches) {
          set({ searchResults: results, searching: false });
        }
      })
      .catch((error) => {
        const current = get();
        const rootStillMatches = searchScope === 'all' || current.selectedRootId === rootId;
        if (current.search.trim() === query && current.searchScope === searchScope && rootStillMatches) {
          set({ error: (error as Error).message, searchResults: [], searching: false });
        }
      });
  },
  setSearchScope: (searchScope: WikiSearchScope) => {
    if (get().searchScope === searchScope) return;
    set({ searchScope, searchResults: [], searching: false });
    const query = get().search;
    if (query.trim()) get().setSearch(query);
  },
  setScope: (scope: WikiScope) => set({ scope }),
}));

function newLocalMessageId(prefix: string): string {
  return `${prefix}_${Date.now().toString(16)}_${Math.random().toString(16).slice(2, 8)}`;
}

function isRuntimeNotFound(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error);
  return /\b404\b|not found/i.test(message);
}
