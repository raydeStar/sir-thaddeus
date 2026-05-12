import { runtimeFetch, readRuntimeMetadata, parseRuntimeJson } from './runtime';
import type {
  ActivityEntry,
  ActivityListResponse,
  DiagnosticsResponse,
  RuntimeLogListResponse,
  RuntimeLogResponse,
  RuntimeLogSummary,
  TurnTraceListResponse,
  TurnTraceResponse,
  TurnTraceSummary,
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

export async function listTurnTraces(limit = 25): Promise<TurnTraceSummary[]> {
  const res = await runtimeFetch(token(), `/api/turns?limit=${limit}`);
  const body = await asJson<TurnTraceListResponse>(res);
  return body.turns;
}

export async function getTurnTrace(messageId: string): Promise<TurnTraceResponse> {
  const res = await runtimeFetch(token(), `/api/turns/${encodeURIComponent(messageId)}/trace`);
  return asJson<TurnTraceResponse>(res);
}

export async function listRuntimeLogs(limit = 25): Promise<RuntimeLogSummary[]> {
  const res = await runtimeFetch(token(), `/api/logs?limit=${limit}`);
  const body = await asJson<RuntimeLogListResponse>(res);
  return body.logs;
}

export async function getRuntimeLog(fileName: string, tail = 400): Promise<RuntimeLogResponse> {
  const res = await runtimeFetch(
    token(),
    `/api/logs/${encodeURIComponent(fileName)}?tail=${tail}`,
  );
  return asJson<RuntimeLogResponse>(res);
}
