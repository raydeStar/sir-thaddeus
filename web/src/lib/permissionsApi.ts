import type { PermissionDeveloperOverride, PermissionPolicy } from '@thaddeus/shared-types';
import { runtimeFetch, readRuntimeMetadata, parseRuntimeJson } from './runtime';

/**
 * Scope of a permission decision: 'group' applies the decision to the whole
 * capability group (historical behavior), 'tool' pins Session/Always grants
 * to the single tool that asked. Missing on the wire = 'group'.
 */
export type PermissionScope = 'group' | 'tool';

export interface PendingPermission {
  id: string;
  tool: string;
  group: string;
  argsJson: string;
  threadId: string;
  turnId: string;
  createdAt: string;
  /** Missing from older runtimes — treat as 'group'. */
  scope?: PermissionScope;
}

export type PermissionResponse = 'deny' | 'once' | 'session' | 'always';

/** One tool row from GET /api/permissions/catalog. */
export interface PermissionCatalogTool {
  /** Canonical snake_case tool name. */
  name: string;
  /** Persisted per-tool override; null = inherits the group policy. */
  override: PermissionPolicy | null;
  /** Static resolution: toolOverride ?? dev-override-if-dangerous ?? groupPolicy. */
  effective: PermissionPolicy;
}

export interface PermissionCatalogGroup {
  /** camelCase group key matching PermissionsSettings fields (screen, files, …). */
  key: string;
  policy: PermissionPolicy;
  tools: PermissionCatalogTool[];
}

export interface PermissionCatalog {
  developerOverride: PermissionDeveloperOverride;
  groups: PermissionCatalogGroup[];
}

function token(): string {
  return readRuntimeMetadata().token;
}

export async function listPendingPermissions(): Promise<PendingPermission[]> {
  const res = await runtimeFetch(token(), '/api/permissions/pending');
  const body = await parseRuntimeJson<{ requests?: PendingPermission[] }>(res);
  return body.requests ?? [];
}

export async function respondToPermission(
  id: string,
  decision: PermissionResponse,
  scope?: PermissionScope,
): Promise<void> {
  const res = await runtimeFetch(token(), '/api/permissions/respond', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(scope ? { id, decision, scope } : { id, decision }),
  });
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`runtime ${res.status}: ${body || res.statusText}`);
  }
}

/** Group → tool inventory with per-tool overrides and effective policies. */
export async function getPermissionCatalog(): Promise<PermissionCatalog> {
  const res = await runtimeFetch(token(), '/api/permissions/catalog');
  return parseRuntimeJson<PermissionCatalog>(res);
}
