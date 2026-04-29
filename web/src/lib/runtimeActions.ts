import { runtimeFetch, readRuntimeMetadata, parseRuntimeJson } from './runtime';

function token(): string {
  return readRuntimeMetadata().token;
}

export interface StopAllResponse {
  applied: boolean;
  current: string;
  stopped: string[];
  errors: string[];
}

export async function stopAllProcesses(): Promise<StopAllResponse> {
  const res = await runtimeFetch(token(), '/api/stop-all', { method: 'POST' });
  return parseRuntimeJson<StopAllResponse>(res);
}

/**
 * Hard kill — tear down sidecars and then ask the runtime to exit. The shell's
 * RuntimeProcessSupervisor watches for that exit and closes the workspace
 * window, so the whole app comes down. Best-effort: any individual call may
 * fail (network already torn down), so we swallow per-step errors and bound
 * each request with a short timeout — once the runtime accepts the stop, it
 * may never finish flushing the response, and we don't want to block the UI.
 */
export async function killApp(): Promise<void> {
  const tk = token();
  // Stop sidecars first so the runtime exit doesn't orphan them. Bounded so
  // the user gets immediate feedback even if the server is mid-shutdown.
  await postWithTimeout(tk, '/api/stop-all', 1500);
  await postWithTimeout(tk, '/api/runtime/stop', 1500);
}

async function postWithTimeout(tk: string, path: string, timeoutMs: number): Promise<void> {
  const ctrl = new AbortController();
  const timer = window.setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    await runtimeFetch(tk, path, { method: 'POST', signal: ctrl.signal });
  } catch {
    // best-effort — server may already be tearing down
  } finally {
    window.clearTimeout(timer);
  }
}