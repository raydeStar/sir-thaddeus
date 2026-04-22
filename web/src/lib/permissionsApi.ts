import { runtimeFetch, readRuntimeMetadata } from './runtime';

export interface PendingPermission {
  id: string;
  tool: string;
  group: string;
  argsJson: string;
  threadId: string;
  turnId: string;
  createdAt: string;
}

export type PermissionResponse = 'deny' | 'once' | 'session' | 'always';

function token(): string {
  return readRuntimeMetadata().token;
}

export async function listPendingPermissions(): Promise<PendingPermission[]> {
  const res = await runtimeFetch(token(), '/api/permissions/pending');
  if (!res.ok) throw new Error(`runtime ${res.status}`);
  const body = await res.json();
  return (body.requests ?? []) as PendingPermission[];
}

export async function respondToPermission(id: string, decision: PermissionResponse): Promise<void> {
  const res = await runtimeFetch(token(), '/api/permissions/respond', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ id, decision }),
  });
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`runtime ${res.status}: ${body || res.statusText}`);
  }
}
