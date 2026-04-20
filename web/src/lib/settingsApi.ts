import { runtimeFetch, readRuntimeMetadata } from './runtime';
import type { SettingsDocument } from '@thaddeus/shared-types';

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

export async function getSettings(): Promise<SettingsDocument> {
  const res = await runtimeFetch(token(), '/api/settings');
  return asJson<SettingsDocument>(res);
}

export async function putSettings(doc: SettingsDocument): Promise<SettingsDocument> {
  const res = await runtimeFetch(token(), '/api/settings', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(doc),
  });
  return asJson<SettingsDocument>(res);
}
