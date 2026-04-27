import { runtimeFetch, readRuntimeMetadata, parseRuntimeJson } from './runtime';
import type { Memo } from '@thaddeus/shared-types';

function token(): string {
  return readRuntimeMetadata().token;
}

const asJson = parseRuntimeJson;

export interface MemoListResponse {
  memos: Memo[];
}

export async function listMemos(): Promise<Memo[]> {
  const res = await runtimeFetch(token(), '/api/memos');
  return (await asJson<MemoListResponse>(res)).memos;
}

export interface CreateMemoInput {
  title?: string;
  body?: string;
  tags?: string[];
  pinned?: boolean;
}

export async function createMemo(input: CreateMemoInput): Promise<Memo> {
  const res = await runtimeFetch(token(), '/api/memos', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<Memo>(res);
}

export interface UpdateMemoInput {
  title?: string;
  body?: string;
  tags?: string[];
  pinned?: boolean;
}

export async function updateMemo(id: string, input: UpdateMemoInput): Promise<Memo> {
  const res = await runtimeFetch(token(), `/api/memos/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<Memo>(res);
}

export async function deleteMemo(id: string): Promise<void> {
  const res = await runtimeFetch(token(), `/api/memos/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });
  if (!res.ok && res.status !== 404) {
    throw new Error(`runtime ${res.status}: ${res.statusText}`);
  }
}
