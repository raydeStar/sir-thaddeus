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
