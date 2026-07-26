// Hand-mirrored from src/Thaddeus.Runtime/Api/MemoryAuditApi.cs.
// These types describe the user-facing audit surface for the
// SQLite-backed semantic memory (facts, events, chunks, profile,
// nuggets) — NOT the old memo / scratchpad types in `memo.ts`.

export interface ProfileDto {
  id: string;
  /** "user" for the primary user, "person" for someone they mentioned. */
  kind: string;
  displayName: string;
  relationship?: string | null;
  /** Semicolon-delimited alternative names. */
  aliases?: string | null;
  /** Free-form JSON blob — preferred name, pronouns, timezone, etc. */
  profileJson: string;
  updatedAt: string;
}

export interface NuggetDto {
  id: string;
  text: string;
  /** Semicolon-wrapped tag list, e.g. ";identity;preference;". */
  tags?: string | null;
  /** True when pinLevel >= 1. */
  pinned: boolean;
  /** 0 = normal, 1 = user-pinned, 2 = system (reserved). */
  pinLevel: number;
  weight: number;
  /** "low" | "med" | "high". */
  sensitivity: string;
  useCount: number;
  lastUsedAt?: string | null;
  createdAt: string;
  updatedAt: string;
  origin?: string | null;
  sourceTurnId?: string | null;
}

export interface FactDto {
  id: string;
  subject: string;
  predicate: string;
  object: string;
  confidence: number;
  weight: number;
  /** "public" | "personal" | "secret". */
  sensitivity: string;
  createdAt: string;
  updatedAt: string;
  origin?: string | null;
  profileId?: string | null;
  sourceTurnId?: string | null;
  sourceRef?: string | null;
}

export interface EventDto {
  id: string;
  type: string;
  title: string;
  summary?: string | null;
  whenIso?: string | null;
  confidence: number;
  weight: number;
  sensitivity: string;
  createdAt: string;
  updatedAt: string;
  origin?: string | null;
  profileId?: string | null;
  sourceTurnId?: string | null;
  sourceRef?: string | null;
}

export interface UpdateNuggetRequest {
  text: string;
  tags?: string | null;
  tagsProvided?: boolean;
}

export interface UpdateFactRequest {
  subject: string;
  predicate: string;
  object: string;
}

export interface MemoryOverviewResponse {
  factCount: number;
  eventCount: number;
  chunkCount: number;
  nuggetCount: number;
  profile?: ProfileDto | null;
}

export interface MemoryPolicyResponse {
  enabled: boolean;
}

export interface MemoryResetResponse {
  rowsRemoved: number;
}

export interface NuggetListResponse {
  items: NuggetDto[];
  totalCount: number;
}

export interface FactListResponse {
  items: FactDto[];
  totalCount: number;
}

export interface EventListResponse {
  items: EventDto[];
  totalCount: number;
}

export interface ProfileListResponse {
  items: ProfileDto[];
}

export interface ReflectionAction {
  /** "deduped_fact" on success, "delete_skipped" on failure. */
  kind: string;
  factId: string;
  reason: string;
  keptFactId: string;
  subject: string;
  predicate: string;
  object: string;
}

export interface ReflectionReport {
  startedAt: string;
  factsScanned: number;
  duplicateGroups: number;
  factsRemoved: number;
  durationMs: number;
  actions: ReflectionAction[];
  error?: string | null;
}
