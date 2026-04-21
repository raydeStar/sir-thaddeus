import { runtimeFetch, readRuntimeMetadata } from './runtime';
import type { Automation, AutomationSchedule, ToolCatalogEntry } from '@thaddeus/shared-types';

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

export interface AutomationListResponse {
  automations: Automation[];
}

export async function listAutomations(): Promise<Automation[]> {
  const res = await runtimeFetch(token(), '/api/automations');
  return (await asJson<AutomationListResponse>(res)).automations;
}

export async function getAutomation(id: string): Promise<Automation> {
  const res = await runtimeFetch(token(), `/api/automations/${encodeURIComponent(id)}`);
  return asJson<Automation>(res);
}

export interface CreateAutomationInput {
  name: string;
  description?: string;
  steps: string[];
  enabled?: boolean;
  allowedTools?: string[];
  schedule?: AutomationSchedule;
}

export async function createAutomation(input: CreateAutomationInput): Promise<Automation> {
  const res = await runtimeFetch(token(), '/api/automations', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<Automation>(res);
}

export interface UpdateAutomationInput {
  name?: string;
  description?: string;
  steps?: string[];
  enabled?: boolean;
  allowedTools?: string[];
  schedule?: AutomationSchedule;
}

export async function updateAutomation(id: string, input: UpdateAutomationInput): Promise<Automation> {
  const res = await runtimeFetch(token(), `/api/automations/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<Automation>(res);
}

export async function deleteAutomation(id: string): Promise<void> {
  const res = await runtimeFetch(token(), `/api/automations/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });
  if (!res.ok && res.status !== 404) throw new Error(`runtime ${res.status}: ${res.statusText}`);
}

export interface AutomationRunResponse {
  automation: Automation;
  threadId: string;
  activityId: string;
}

export async function runAutomation(id: string): Promise<AutomationRunResponse> {
  const res = await runtimeFetch(token(), `/api/automations/${encodeURIComponent(id)}/run`, {
    method: 'POST',
  });
  return asJson<AutomationRunResponse>(res);
}

export async function listToolCatalog(): Promise<ToolCatalogEntry[]> {
  const res = await runtimeFetch(token(), '/api/automations/tools');
  const body = await asJson<{ tools: ToolCatalogEntry[] }>(res);
  return body.tools;
}

export interface SuggestToolsInput {
  name?: string;
  description?: string;
  steps: string[];
}

export interface SuggestToolsResult {
  tools: string[];
  note?: string | null;
}

export async function suggestTools(input: SuggestToolsInput): Promise<SuggestToolsResult> {
  const res = await runtimeFetch(token(), '/api/automations/suggest-tools', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<SuggestToolsResult>(res);
}

export interface DraftAutomationResult {
  name?: string | null;
  description?: string | null;
  steps: string[];
  note?: string | null;
}

export async function draftAutomation(goal: string): Promise<DraftAutomationResult> {
  const res = await runtimeFetch(token(), '/api/automations/draft', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ goal }),
  });
  return asJson<DraftAutomationResult>(res);
}
