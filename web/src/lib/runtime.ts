/**
 * Reads the runtime metadata (bearer token, port, version, route hint) the runtime
 * injects into <meta> tags before serving index.html. Falls back to empty values for
 * `vite dev`, where there is no runtime in the loop.
 */
export interface RuntimeMetadata {
  token: string;
  port: string;
  version: string;
  route: string;
}

export function readRuntimeMetadata(): RuntimeMetadata {
  return {
    token: readMeta('thaddeus-runtime-token'),
    port: readMeta('thaddeus-runtime-port'),
    version: readMeta('thaddeus-runtime-version') || 'dev',
    route: readMeta('thaddeus-runtime-route') || 'workspace',
  };
}

function readMeta(name: string): string {
  if (typeof document === 'undefined') return '';
  const el = document.querySelector(`meta[name="${name}"]`);
  return el?.getAttribute('content') ?? '';
}

/**
 * Build the WebSocket URL for the runtime. Uses the current page's host so it works
 * both inside Photino and in the browser. Bearer token rides as ?access_token= per
 * RFC 6750 §2.3 (Sir Thaddeus restricts that query-param transport to /ws only).
 */
export function buildRuntimeWebSocketUrl(token: string): string {
  if (typeof window === 'undefined') return '';
  const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  const path = `/ws${token ? `?access_token=${encodeURIComponent(token)}` : ''}`;
  return `${proto}//${window.location.host}${path}`;
}

/** Thin fetch wrapper that includes the bearer token. */
export async function runtimeFetch(token: string, path: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  if (token) headers.set('Authorization', `Bearer ${token}`);
  return fetch(path, { ...init, headers });
}

/**
 * Shared JSON decoder for runtime API responses. Guards against the vite-
 * dev / misconfigured-proxy case where the backend's JSON endpoint gets
 * shadowed by the SPA index.html fallback — `res.ok` is true but the body
 * is HTML. Without this check, every page surfaces the cryptic
 * `Unexpected token '<', "<!doctype "... is not valid JSON` error from
 * `res.json()`. Centralizing the decoder also normalizes the error shape
 * so UI banners can pattern-match a single marker string.
 */
export async function parseRuntimeJson<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`runtime ${res.status}: ${body || res.statusText}`);
  }
  const contentType = res.headers.get('content-type') ?? '';
  if (!contentType.includes('json')) {
    // Drain the body so the browser can reuse the connection, then throw
    // a user-facing error instead of the raw JSON.parse failure.
    await res.text().catch(() => '');
    throw new Error(
      'Runtime API unavailable — got a non-JSON response. ' +
      'Make sure the backend is running and reachable.'
    );
  }
  return (await res.json()) as T;
}
