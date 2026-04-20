import { runtimeFetch, readRuntimeMetadata } from './runtime';
import type {
  ChatThread,
  ThreadListResponse,
  ThreadSummary,
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

export async function appendMessage(threadId: string, text: string): Promise<ChatThread> {
  const res = await runtimeFetch(token(), `/api/threads/${encodeURIComponent(threadId)}/messages`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
  });
  const body = await asJson<{ message: unknown; thread: ChatThread }>(res);
  return body.thread;
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
