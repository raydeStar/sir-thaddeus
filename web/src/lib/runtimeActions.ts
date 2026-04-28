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
 * fail (network already torn down), so we swallow per-step errors.
 */
export async function killApp(): Promise<void> {
  // Stop sidecars first so the runtime exit doesn't orphan them.
  try {
    await runtimeFetch(token(), '/api/stop-all', { method: 'POST' });
  } catch {
    // best-effort
  }
  try {
    await runtimeFetch(token(), '/api/runtime/stop', { method: 'POST' });
  } catch {
    // best-effort — runtime may already be tearing down
  }
}