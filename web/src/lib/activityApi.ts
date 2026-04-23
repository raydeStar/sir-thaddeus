import { runtimeFetch, readRuntimeMetadata, parseRuntimeJson } from './runtime';
import type {
  ActivityEntry,
  ActivityListResponse,
  DiagnosticsResponse,
} from '@thaddeus/shared-types';

function token(): string {
  return readRuntimeMetadata().token;
}

const asJson = parseRuntimeJson;

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
