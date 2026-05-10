import { runtimeFetch, readRuntimeMetadata } from './runtime';

function token(): string {
  return readRuntimeMetadata().token;
}

export function isExternalUrl(value?: string | null): value is string {
  if (!value) return false;
  return /^https?:\/\//i.test(value);
}

export async function openExternalUrl(url: string): Promise<void> {
  if (!isExternalUrl(url)) {
    return;
  }

  const tk = token();
  if (tk) {
    try {
      const res = await runtimeFetch(tk, '/api/runtime/open-external-url', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ url }),
      });
      if (res.ok) return;
    } catch {
      // Fall through to the browser fallback below.
    }
  }

  window.open(url, '_blank', 'noopener,noreferrer');
}
