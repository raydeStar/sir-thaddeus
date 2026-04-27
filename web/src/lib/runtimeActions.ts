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