import { create } from 'zustand';
import * as api from '../lib/wikiApi';
import type { WikiPageDocument, WikiRevision, WikiRoot, WikiTree } from '../lib/wikiApi';

export type WikiScope = 'root' | 'folder' | 'page';

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
  draft: string;
  dirty: boolean;
  loading: boolean;
  saving: boolean;
  error: string | null;

  loadRoots: () => Promise<void>;
  selectRoot: (rootId: string) => Promise<void>;
  selectFolder: (folderId: string | null) => void;
  selectPage: (pageId: string) => Promise<void>;
  createRoot: () => Promise<void>;
  createFolder: () => Promise<void>;
  createPage: () => Promise<void>;
  savePage: () => Promise<void>;
  setDraft: (markdown: string) => void;
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
  draft: '',
  dirty: false,
  loading: false,
  saving: false,
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
        set({ tree: null, page: null, revisions: [], selectedRootId: null, selectedFolderId: null, selectedPageId: null, scope: 'root', draft: '', dirty: false });
      }
    } catch (error) {
      set({ error: (error as Error).message, loading: false });
    }
  },

  selectRoot: async (rootId: string) => {
    set({ loading: true, error: null, selectedRootId: rootId, selectedFolderId: null, selectedPageId: null, page: null, revisions: [], draft: '', dirty: false, scope: 'root' });
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
        dirty: false,
        scope: 'page',
        loading: false,
      });
    } catch (error) {
      set({ error: (error as Error).message, loading: false });
    }
  },

  createRoot: async () => {
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

  createPage: async () => {
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
      set({ page: saved, tree, revisions, draft: saved.markdown, dirty: false });
    } catch (error) {
      set({ error: (error as Error).message });
    } finally {
      set({ saving: false });
    }
  },

  setDraft: (markdown: string) => set({ draft: markdown, dirty: true }),
  setSearch: (search: string) => set({ search }),
  setScope: (scope: WikiScope) => set({ scope }),
}));