import { runtimeFetch, readRuntimeMetadata, parseRuntimeJson } from './runtime';
import type {
  ActivityEntry,
  ActivityListResponse,
  AssistantInsightsResponse,
  AuditEvent,
  AuditTrailResponse,
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

export async function getAssistantInsights(limit = 2_000): Promise<AssistantInsightsResponse> {
  const res = await runtimeFetch(token(), `/api/insights?limit=${limit}`);
  return asJson<AssistantInsightsResponse>(res);
}

export async function listAuditEvents(limit = 200): Promise<AuditEvent[]> {
  const res = await runtimeFetch(token(), `/api/audit?limit=${limit}`);
  const body = await asJson<AuditTrailResponse>(res);
  return body.events;
}

export async function exportAuditTrail(limit = 10_000): Promise<Blob> {
  const res = await runtimeFetch(token(), `/api/audit/export?limit=${limit}`);
  if (!res.ok) {
    throw new Error(`Audit export failed (${res.status})`);
  }
  return res.blob();
}

export async function recordAssistantOutcomeFeedback(input: {
  messageId: string;
  success: boolean;
  confidence: number;
  evidenceLevel: string;
}): Promise<void> {
  const res = await runtimeFetch(token(), '/api/insights/feedback', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  await asJson<{ recorded: boolean }>(res);
}
