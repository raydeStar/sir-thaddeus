import { runtimeFetch, readRuntimeMetadata } from './runtime';
import type {
  ActivityEntry,
  ActivityListResponse,
  DiagnosticsResponse,
} from '@thaddeus/shared-types';

function token(): string {
  return readRuntimeMetadata().token;
}

async function asJson<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`runtime ${res.status}: ${body || res.statusText}`);
  }
  return (await res.json()) as T;
}

export async function listActivity(limit = 50): Promise<ActivityEntry[]> {
  const res = await runtimeFetch(token(), `/api/activity?limit=${limit}`);
  const body = await asJson<ActivityListResponse>(res);
  return body.entries;
}

export async function getActivityEntry(id: string): Promise<ActivityEntry> {
  const res = await runtimeFetch(token(), `/api/activity/${encodeURIComponent(id)}`);
  return asJson<ActivityEntry>(res);
}

export async function getDiagnostics(): Promise<DiagnosticsResponse> {
  const res = await runtimeFetch(token(), '/api/diagnostics');
  return asJson<DiagnosticsResponse>(res);
}
