import { runtimeFetch, readRuntimeMetadata, parseRuntimeJson } from './runtime';
import type {
  ChatThread,
  ThreadListResponse,
  ThreadSummary,
  TurnRunSnapshot,
  WorkPlanStep,
} from '@thaddeus/shared-types';

export type WikiChatContextInput =
  | { mode: 'none' }
  | { mode: 'all' }
  | { mode: 'root'; rootId: string }
  | { mode: 'folder'; rootId: string; folderId: string }
  | { mode: 'page'; pageId: string };

export type WikiMutationTargetInput =
  | { mode: 'root'; rootId: string }
  | { mode: 'page'; pageId: string };

function token(): string {
  return readRuntimeMetadata().token;
}

const asJson = parseRuntimeJson;

export async function listThreads(): Promise<ThreadSummary[]> {
  const res = await runtimeFetch(token(), '/api/threads');
  const body = await asJson<ThreadListResponse>(res);
  return body.threads;
}

export async function getThread(id: string): Promise<ChatThread> {
  const res = await runtimeFetch(token(), `/api/threads/${encodeURIComponent(id)}`);
  return asJson<ChatThread>(res);
}

export async function createThread(title?: string): Promise<ChatThread> {
  const res = await runtimeFetch(token(), '/api/threads', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title: title ?? '' }),
  });
  return asJson<ChatThread>(res);
}

export async function appendMessage(
  threadId: string,
  text: string,
  wikiContext?: WikiChatContextInput,
  options?: { ephemeralMemory?: boolean; wikiMutationTarget?: WikiMutationTargetInput },
): Promise<{ thread: ChatThread; run: TurnRunSnapshot }> {
  const res = await runtimeFetch(token(), `/api/threads/${encodeURIComponent(threadId)}/messages`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      text,
      wikiContext,
      wikiMutationTarget: options?.wikiMutationTarget,
      ephemeralMemory: options?.ephemeralMemory ?? false,
    }),
  });
  const body = await asJson<{ message: unknown; thread: ChatThread; run: TurnRunSnapshot }>(res);
  return { ...body, run: normalizeRun(body.run) };
}

export async function listRuns(threadId?: string): Promise<TurnRunSnapshot[]> {
  const query = threadId ? `?threadId=${encodeURIComponent(threadId)}` : '';
  const res = await runtimeFetch(token(), `/api/runs${query}`);
  const body = await asJson<{ runs: TurnRunSnapshot[] }>(res);
  return body.runs.map(normalizeRun);
}

export async function retryLatestResponse(
  threadId: string,
  options?: { ephemeralMemory?: boolean },
): Promise<{ thread: ChatThread; run: TurnRunSnapshot }> {
  const res = await runtimeFetch(token(), `/api/threads/${encodeURIComponent(threadId)}/messages/retry`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ephemeralMemory: options?.ephemeralMemory ?? false }),
  });
  const body = await asJson<{ thread: ChatThread; run: TurnRunSnapshot }>(res);
  return { ...body, run: normalizeRun(body.run) };
}

async function controlRun(runId: string, action: 'pause' | 'resume' | 'cancel'): Promise<TurnRunSnapshot> {
  const res = await runtimeFetch(
    token(),
    `/api/runs/${encodeURIComponent(runId)}/${action}`,
    { method: 'POST' },
  );
  return normalizeRun(await asJson<TurnRunSnapshot>(res));
}

export const pauseRun = (runId: string) => controlRun(runId, 'pause');
export const resumeRun = (runId: string) => controlRun(runId, 'resume');
export const cancelRun = (runId: string) => controlRun(runId, 'cancel');

export async function takeOverRun(runId: string): Promise<TurnRunSnapshot> {
  const res = await runtimeFetch(
    token(),
    `/api/runs/${encodeURIComponent(runId)}/take-over`,
    { method: 'POST' },
  );
  return normalizeRun(await asJson<TurnRunSnapshot>(res));
}

export async function redirectRun(runId: string, instruction: string): Promise<TurnRunSnapshot> {
  const res = await runtimeFetch(
    token(),
    `/api/runs/${encodeURIComponent(runId)}/redirect`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ instruction }),
    },
  );
  return normalizeRun(await asJson<TurnRunSnapshot>(res));
}

export async function approvePlan(runId: string, expectedVersion: number): Promise<TurnRunSnapshot> {
  const res = await runtimeFetch(
    token(),
    `/api/runs/${encodeURIComponent(runId)}/plan/approve`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ expectedVersion }),
    },
  );
  return normalizeRun(await asJson<TurnRunSnapshot>(res));
}

export async function editPlan(
  runId: string,
  expectedVersion: number,
  steps: WorkPlanStep[],
): Promise<TurnRunSnapshot> {
  const res = await runtimeFetch(
    token(),
    `/api/runs/${encodeURIComponent(runId)}/plan`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ expectedVersion, steps }),
    },
  );
  return normalizeRun(await asJson<TurnRunSnapshot>(res));
}

function normalizeRun(run: TurnRunSnapshot): TurnRunSnapshot {
  return {
    ...run,
    state: String(run.state).toLowerCase() as TurnRunSnapshot['state'],
    plan: run.plan
      ? {
          ...run.plan,
          risk: String(run.plan.risk).toLowerCase() as typeof run.plan.risk,
          steps: run.plan.steps.map((step) => ({
            ...step,
            capability: String(step.capability).toLowerCase() as typeof step.capability,
            risk: String(step.risk).toLowerCase() as typeof step.risk,
            status: String(step.status).toLowerCase() as typeof step.status,
          })),
        }
      : null,
  };
}

export async function deleteThread(id: string): Promise<void> {
  const res = await runtimeFetch(token(), `/api/threads/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });
  if (!res.ok && res.status !== 404) {
    throw new Error(`runtime ${res.status}: ${res.statusText}`);
  }
}

export async function patchThread(
  id: string,
  patch: { title?: string; pinned?: boolean },
): Promise<ChatThread> {
  const res = await runtimeFetch(token(), `/api/threads/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(patch),
  });
  return asJson<ChatThread>(res);
}
