import { create } from 'zustand';

/**
 * Persists the chat composer's wiki context selection across composer mounts
 * (home → thread navigation, route remounts) so a user's chosen scope isn't
 * silently dropped after every turn. Stored as the composer's option `value`
 * string (e.g. "page:abc123") so the same instance can rehydrate it.
 */
interface WikiContextStoreState {
  value: string;
  mutationTargetValue: string;
  mutationOperationValue: string;
  setValue: (value: string) => void;
  setMutationTargetValue: (value: string) => void;
  setMutationOperationValue: (value: string) => void;
  clear: () => void;
}

export const useWikiContextStore = create<WikiContextStoreState>((set) => ({
  value: '',
  mutationTargetValue: '',
  mutationOperationValue: '',
  setValue: (value) => set({ value }),
  setMutationTargetValue: (mutationTargetValue) => set({ mutationTargetValue }),
  setMutationOperationValue: (mutationOperationValue) => set({ mutationOperationValue }),
  clear: () => set({ value: '', mutationTargetValue: '', mutationOperationValue: '' }),
}));
