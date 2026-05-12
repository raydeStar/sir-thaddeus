import { runtimeFetch, readRuntimeMetadata, parseRuntimeJson } from './runtime';
import type {
  EventListResponse,
  FactListResponse,
  MemoryOverviewResponse,
  NuggetDto,
  NuggetListResponse,
  ProfileListResponse,
  ReflectionReport,
  FactDto,
  UpdateFactRequest,
  UpdateNuggetRequest,
} from '@thaddeus/shared-types';

function token(): string {
  return readRuntimeMetadata().token;
}

/** Counts + the user profile card. Cheap; reads only TotalCounts, no rows. */
export async function getMemoryOverview(): Promise<MemoryOverviewResponse> {
  const res = await runtimeFetch(token(), '/api/memory/overview');
  return parseRuntimeJson<MemoryOverviewResponse>(res);
}

export async function listNuggets(filter?: string, limit = 50): Promise<NuggetListResponse> {
  const qs = new URLSearchParams();
  if (filter) qs.set('filter', filter);
  qs.set('limit', String(limit));
  const res = await runtimeFetch(token(), `/api/memory/nuggets?${qs.toString()}`);
  return parseRuntimeJson<NuggetListResponse>(res);
}

export async function listFacts(filter?: string, limit = 50): Promise<FactListResponse> {
  const qs = new URLSearchParams();
  if (filter) qs.set('filter', filter);
  qs.set('limit', String(limit));
  const res = await runtimeFetch(token(), `/api/memory/facts?${qs.toString()}`);
  return parseRuntimeJson<FactListResponse>(res);
}

export async function listEvents(filter?: string, limit = 50): Promise<EventListResponse> {
  const qs = new URLSearchParams();
  if (filter) qs.set('filter', filter);
  qs.set('limit', String(limit));
  const res = await runtimeFetch(token(), `/api/memory/events?${qs.toString()}`);
  return parseRuntimeJson<EventListResponse>(res);
}

export async function listProfiles(): Promise<ProfileListResponse> {
  const res = await runtimeFetch(token(), '/api/memory/profiles');
  return parseRuntimeJson<ProfileListResponse>(res);
}

export async function deleteNugget(id: string): Promise<void> {
  const res = await runtimeFetch(token(), `/api/memory/nuggets/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });
  if (!res.ok && res.status !== 204) {
    throw new Error(`delete nugget failed (${res.status})`);
  }
}

export async function deleteFact(id: string): Promise<void> {
  const res = await runtimeFetch(token(), `/api/memory/facts/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });
  if (!res.ok && res.status !== 204) {
    throw new Error(`delete fact failed (${res.status})`);
  }
}

export async function deleteEvent(id: string): Promise<void> {
  const res = await runtimeFetch(token(), `/api/memory/events/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });
  if (!res.ok && res.status !== 204) {
    throw new Error(`delete event failed (${res.status})`);
  }
}

export async function setNuggetPinned(id: string, pinned: boolean): Promise<NuggetDto> {
  const res = await runtimeFetch(token(), `/api/memory/nuggets/${encodeURIComponent(id)}/pin`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pinned }),
  });
  return parseRuntimeJson<NuggetDto>(res);
}

export async function updateNugget(id: string, request: UpdateNuggetRequest): Promise<NuggetDto> {
  const res = await runtimeFetch(token(), `/api/memory/nuggets/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
  return parseRuntimeJson<NuggetDto>(res);
}

export async function updateFact(id: string, request: UpdateFactRequest): Promise<FactDto> {
  const res = await runtimeFetch(token(), `/api/memory/facts/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
  return parseRuntimeJson<FactDto>(res);
}

/**
 * Trigger a manual reflection pass. The server dedupes facts whose
 * normalized (subject, predicate, object) triple matches another non-
 * deleted fact, keeping the highest-confidence version. Returns a report
 * the UI surfaces so the user can see exactly what was consolidated.
 */
export async function runReflection(): Promise<ReflectionReport> {
  const res = await runtimeFetch(token(), '/api/memory/reflect', { method: 'POST' });
  return parseRuntimeJson<ReflectionReport>(res);
}
