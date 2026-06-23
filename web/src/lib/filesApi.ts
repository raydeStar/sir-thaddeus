import { runtimeFetch, readRuntimeMetadata, parseRuntimeJson } from './runtime';

function token(): string {
  return readRuntimeMetadata().token;
}

export interface FolderSuggestion {
  /** Stable id ("documents" / "downloads" / "desktop"). */
  id: string;
  /** Short human-readable label shown in the UI. */
  label: string;
  /** One-line description shown beneath the label. */
  description: string;
  /** Absolute path resolved by the runtime for the current OS. */
  path: string;
  /** True when the directory actually exists on disk. */
  exists: boolean;
  /** Whether the suggestion should start checked in the onboarding wizard. */
  defaultEnabled: boolean;
}

export interface FolderSuggestionsResponse {
  suggestions: FolderSuggestion[];
}

/** Fetch the runtime's per-OS folder suggestions for the onboarding wizard. */
export async function getFolderSuggestions(): Promise<FolderSuggestion[]> {
  const res = await runtimeFetch(token(), '/api/files/folder-suggestions');
  const body = await parseRuntimeJson<FolderSuggestionsResponse>(res);
  return body.suggestions;
}
