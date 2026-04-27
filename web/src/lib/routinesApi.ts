import { runtimeFetch, readRuntimeMetadata, parseRuntimeJson } from './runtime';
import type { Routine, RoutineRun } from '@thaddeus/shared-types';

function token(): string {
  return readRuntimeMetadata().token;
}

const asJson = parseRuntimeJson;

export interface RoutineListResponse {
  routines: Routine[];
}
export interface RoutineRunListResponse {
  runs: RoutineRun[];
}

// ── Routines ────────────────────────────────────────────────────────────

export async function listRoutines(): Promise<Routine[]> {
  const res = await runtimeFetch(token(), '/api/routines');
  return (await asJson<RoutineListResponse>(res)).routines;
}

export async function getRoutine(id: string): Promise<Routine> {
  const res = await runtimeFetch(token(), `/api/routines/${encodeURIComponent(id)}`);
  return asJson<Routine>(res);
}

export interface RoutineChecklistItemInput {
  id?: string;
  text: string;
  sortOrder?: number;
}

export interface CreateRoutineInput {
  name: string;
  description?: string;
  checklistItems: RoutineChecklistItemInput[];
  promptTemplate?: string;
  enabled?: boolean;
}

export async function createRoutine(input: CreateRoutineInput): Promise<Routine> {
  const res = await runtimeFetch(token(), '/api/routines', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<Routine>(res);
}

export interface UpdateRoutineInput {
  name?: string;
  description?: string;
  checklistItems?: RoutineChecklistItemInput[];
  promptTemplate?: string;
  enabled?: boolean;
}

export async function updateRoutine(id: string, input: UpdateRoutineInput): Promise<Routine> {
  const res = await runtimeFetch(token(), `/api/routines/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<Routine>(res);
}

export async function deleteRoutine(id: string): Promise<void> {
  const res = await runtimeFetch(token(), `/api/routines/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });
  if (!res.ok && res.status !== 404) {
    throw new Error(`runtime ${res.status}: ${res.statusText}`);
  }
}

// ── Runs ────────────────────────────────────────────────────────────────

export async function startRun(routineId: string): Promise<RoutineRun> {
  const res = await runtimeFetch(token(), `/api/routines/${encodeURIComponent(routineId)}/runs`, {
    method: 'POST',
  });
  return asJson<RoutineRun>(res);
}

export async function listRuns(routineId: string): Promise<RoutineRun[]> {
  const res = await runtimeFetch(token(), `/api/routines/${encodeURIComponent(routineId)}/runs`);
  return (await asJson<RoutineRunListResponse>(res)).runs;
}

export async function getRun(runId: string): Promise<RoutineRun> {
  const res = await runtimeFetch(token(), `/api/routine-runs/${encodeURIComponent(runId)}`);
  return asJson<RoutineRun>(res);
}

export interface RoutineRunItemUpdate {
  checklistItemId: string;
  isCompleted: boolean;
}

export interface UpdateRunInput {
  itemUpdates?: RoutineRunItemUpdate[];
  userNote?: string;
}

export async function updateRun(runId: string, input: UpdateRunInput): Promise<RoutineRun> {
  const res = await runtimeFetch(token(), `/api/routine-runs/${encodeURIComponent(runId)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<RoutineRun>(res);
}

export async function completeRun(runId: string, userNote?: string): Promise<RoutineRun> {
  const res = await runtimeFetch(token(), `/api/routine-runs/${encodeURIComponent(runId)}/complete`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ userNote: userNote ?? null }),
  });
  return asJson<RoutineRun>(res);
}

export async function discardRun(runId: string): Promise<void> {
  const res = await runtimeFetch(token(), `/api/routine-runs/${encodeURIComponent(runId)}`, {
    method: 'DELETE',
  });
  if (!res.ok && res.status !== 404) {
    throw new Error(`runtime ${res.status}: ${res.statusText}`);
  }
}
