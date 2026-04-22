/**
 * A user-curated memo (Phase 7.1). Mirrors {@link Memo} on the C# side.
 */
export interface Memo {
  id: string;
  title: string;
  body: string;
  tags: string[];
  pinned: boolean;
  createdAt: string;
  updatedAt: string;
}
