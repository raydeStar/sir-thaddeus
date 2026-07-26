import { create } from 'zustand';

interface WorkbenchState {
  pageId: string | null;
  sourceThreadId: string | null;
  openWikiPage: (pageId: string, sourceThreadId?: string | null) => void;
  close: () => void;
}

export const useWorkbenchStore = create<WorkbenchState>((set) => ({
  pageId: null,
  sourceThreadId: null,
  openWikiPage: (pageId, sourceThreadId = null) => {
    if (!pageId) return;
    set({ pageId, sourceThreadId });
  },
  close: () => set({ pageId: null, sourceThreadId: null }),
}));
