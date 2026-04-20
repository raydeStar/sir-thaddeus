import { runtimeFetch, readRuntimeMetadata } from './runtime';
import type { Automation } from '@thaddeus/shared-types';

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

export async function runAutomation(id: string): Promise<Automation> {
  const res = await runtimeFetch(token(), `/api/automations/${encodeURIComponent(id)}/run`, {
    method: 'POST',
  });
  return asJson<Automation>(res);
}
