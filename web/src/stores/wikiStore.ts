import { create } from 'zustand';
import * as api from '../lib/wikiApi';
import type { WikiPageDocument, WikiPageDraft, WikiRevision, WikiRoot, WikiSearchResult, WikiSelectionRewriteDraft, WikiTree } from '../lib/wikiApi';

export type WikiScope = 'root' | 'folder' | 'page';

export interface WikiPageChatMessage {
  id: string;
  role: 'user' | 'assistant';
  text: string;
  createdAt: string;
}

interface WikiStoreState {
  roots: WikiRoot[];
  tree: WikiTree | null;
  page: WikiPageDocument | null;
  revisions: WikiRevision[];
  selectedRootId: string | null;
  selectedFolderId: string | null;
  selectedPageId: string | null;
  scope: WikiScope;
  search: string;
  searchResults: WikiSearchResult[];
  draft: string;
  pageChatMessages: WikiPageChatMessage[];
  pageChatDraft: WikiPageDraft | null;
  selectedText: string;
  selectionRewriteDraft: WikiSelectionRewriteDraft | null;
  dirty: boolean;
  loading: boolean;
  saving: boolean;
  searching: boolean;
  pageAssistantBusy: boolean;
  error: string | null;

  loadRoots: () => Promise<void>;
  selectRoot: (rootId: string) => Promise<void>;
  selectFolder: (folderId: string | null) => void;
  selectPage: (pageId: string) => Promise<void>;
  createRoot: () => Promise<void>;
  createFolder: () => Promise<void>;
  renameFolder: (folderId: string, name: string) => Promise<void>;
  createPage: () => Promise<void>;
  savePage: () => Promise<void>;
  renamePage: (title: string) => Promise<void>;
  discardDraft: () => void;
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
  setScope: (scope: WikiScope) => void;
}

export const useWikiStore = create<WikiStoreState>((set, get) => ({
  roots: [],
  tree: null,
  page: null,
  revisions: [],
  selectedRootId: null,
  selectedFolderId: null,
  selectedPageId: null,
  scope: 'root',
  search: '',
  searchResults: [],
  draft: '',
  pageChatMessages: [],
  pageChatDraft: null,
  selectedText: '',
  selectionRewriteDraft: null,
  dirty: false,
  loading: false,
  saving: false,
  searching: false,
  pageAssistantBusy: false,
  error: null,

  loadRoots: async () => {
    set({ loading: true, error: null });
    try {
      const roots = await api.listWikiRoots();
      set({ roots, loading: false });
      const selectedRootId = get().selectedRootId;
      const selectedRootStillExists = selectedRootId ? roots.some((root) => root.id === selectedRootId) : false;
      if (selectedRootStillExists && selectedRootId) {
        await get().selectRoot(selectedRootId);
      } else if (roots.length > 0) {
        await get().selectRoot(roots[0].id);
      } else {
        set({ tree: null, page: null, revisions: [], selectedRootId: null, selectedFolderId: null, selectedPageId: null, scope: 'root', search: '', searchResults: [], draft: '', pageChatMessages: [], pageChatDraft: null, selectedText: '', selectionRewriteDraft: null, dirty: false });
      }
    } catch (error) {
      set({ error: (error as Error).message, loading: false });
    }
  },

  selectRoot: async (rootId: string) => {
    if (get().dirty) {
      set({ error: 'Save or discard changes before switching roots.' });
      return;
    }
    set({ loading: true, error: null, selectedRootId: rootId, selectedFolderId: null, selectedPageId: null, page: null, revisions: [], search: '', searchResults: [], searching: false, draft: '', pageChatMessages: [], pageChatDraft: null, selectedText: '', selectionRewriteDraft: null, dirty: false, scope: 'root' });
    try {
      const tree = await api.getWikiTree(rootId);
      set({ tree, loading: false });
      const firstPage = tree.pages[0];
      if (firstPage) {
        await get().selectPage(firstPage.id);
      }
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
    set({ loading: true, error: null, selectedPageId: pageId });
    try {
      const page = await api.getWikiPage(pageId);
      const revisions = await api.listWikiRevisions(pageId);
      set({
        page,
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
      set({ error: (error as Error).message, loading: false });
    }
  },

  createRoot: async () => {
    if (get().dirty) {
      set({ error: 'Save or discard changes before creating a root.' });
      return;
    }
    set({ saving: true, error: null });
    try {
      const created = await api.createWikiRoot({ name: `Wiki ${new Date().toLocaleDateString()}` });
      set((state) => ({ roots: [created, ...state.roots] }));
      await get().selectRoot(created.id);
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  createFolder: async () => {
    const rootId = get().selectedRootId;
    if (!rootId) return;
    set({ saving: true, error: null });
    try {
      const created = await api.createWikiFolder(rootId, { name: 'New Folder', parentFolderId: null });
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

  createPage: async () => {
    if (get().dirty) {
      set({ error: 'Save or discard changes before creating a page.' });
      return;
    }
    const rootId = get().selectedRootId;
    if (!rootId) return;
    const folderId = get().selectedFolderId;
    set({ saving: true, error: null });
    try {
      const created = await api.createWikiPage(rootId, {
        title: 'Untitled Page',
        folderId,
        markdown: '# Untitled Page\n',
      });
      const tree = await api.getWikiTree(rootId);
      const revisions = await api.listWikiRevisions(created.page.id);
      set({
        tree,
        page: created,
        revisions,
        selectedPageId: created.page.id,
        selectedFolderId: created.page.folderId,
        draft: created.markdown,
        pageChatMessages: [],
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

  savePage: async () => {
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
      const revisions = await api.listWikiRevisions(saved.page.id);
      set({ page: saved, tree, revisions, draft: saved.markdown, selectedText: '', selectionRewriteDraft: null, dirty: false });
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
      const revisions = await api.listWikiRevisions(renamed.page.id);
      set({ page: renamed, tree, revisions, draft: renamed.markdown, selectedPageId: renamed.page.id, selectedText: '', selectionRewriteDraft: null, dirty: false });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  discardDraft: () => {
    const current = get().page;
    if (!current) return;
    set({ draft: current.markdown, selectedText: '', selectionRewriteDraft: null, dirty: false, error: null });
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
      const revisions = await api.listWikiRevisions(restored.page.id);
      set({
        page: restored,
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
          { id: reply.messageId, role: 'assistant', text: reply.answer, createdAt: reply.createdAt },
        ],
      }));
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ pageAssistantBusy: false });
    }
  },

  draftPage: async (instruction: string) => {
    const current = get().page;
    const trimmed = instruction.trim();
    if (!current || !trimmed) return;
    set({ pageAssistantBusy: true, error: null, pageChatDraft: null });
    try {
      const draft = await api.draftWikiPage(current.page.id, { instruction: trimmed, scope: get().scope });
      set((state) => ({
        pageChatDraft: draft,
        pageChatMessages: [
          ...state.pageChatMessages,
          { id: newLocalMessageId('user'), role: 'user', text: trimmed, createdAt: new Date().toISOString() },
          { id: draft.messageId, role: 'assistant', text: draft.assistantText, createdAt: draft.createdAt },
        ],
      }));
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ pageAssistantBusy: false });
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
      const revisions = await api.listWikiRevisions(saved.page.id);
      set({ page: saved, tree, revisions, draft: saved.markdown, selectedText: '', selectionRewriteDraft: null, dirty: false, pageChatDraft: null });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  clearPageDraft: () => set({ pageChatDraft: null }),

  rewriteSelection: async (instruction: string) => {
    const current = get().page;
    const trimmed = instruction.trim();
    const selectedText = get().selectedText.trim();
    if (!current || !trimmed || !selectedText) return;
    set({ pageAssistantBusy: true, error: null, selectionRewriteDraft: null });
    try {
      const draft = await api.rewriteWikiSelection(current.page.id, {
        selectedText,
        instruction: trimmed,
        expectedVersion: current.page.version,
        scope: get().scope,
      });
      set((state) => ({
        selectionRewriteDraft: draft,
        pageChatMessages: [
          ...state.pageChatMessages,
          { id: newLocalMessageId('user'), role: 'user', text: `Rewrite selection: ${trimmed}`, createdAt: new Date().toISOString() },
          { id: draft.messageId, role: 'assistant', text: draft.assistantText, createdAt: draft.createdAt },
        ],
      }));
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ pageAssistantBusy: false });
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
      const revisions = await api.listWikiRevisions(saved.page.id);
      set({ page: saved, tree, revisions, draft: saved.markdown, selectedText: '', selectionRewriteDraft: null, dirty: false });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  clearSelectionRewrite: () => set({ selectionRewriteDraft: null }),

  setDraft: (markdown: string) => set({ draft: markdown, dirty: true, selectionRewriteDraft: null }),
  setSelectedText: (text: string) => set({ selectedText: text }),
  setSearch: (search: string) => {
    set({ search });
    const query = search.trim();
    const rootId = get().selectedRootId;
    if (!query || !rootId) {
      set({ searchResults: [], searching: false });
      return;
    }

    set({ searching: true, error: null });
    void api.searchWiki(rootId, query)
      .then((results) => {
        if (get().search.trim() === query && get().selectedRootId === rootId) {
          set({ searchResults: results, searching: false });
        }
      })
      .catch((error) => {
        if (get().search.trim() === query && get().selectedRootId === rootId) {
          set({ error: (error as Error).message, searchResults: [], searching: false });
        }
      });
  },
  setScope: (scope: WikiScope) => set({ scope }),
}));

function newLocalMessageId(prefix: string): string {
  return `${prefix}_${Date.now().toString(16)}_${Math.random().toString(16).slice(2, 8)}`;
}