import { runtimeFetch, readRuntimeMetadata } from './runtime';

function token(): string {
  return readRuntimeMetadata().token;
}

export async function synthesizeSpeech(text: string): Promise<Blob> {
  const res = await runtimeFetch(token(), '/api/voice/tts', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
  });
  if (!res.ok) {
    throw new Error(await readVoiceError(res));
  }
  return res.blob();
}

async function readVoiceError(res: Response): Promise<string> {
  const body = await res.text().catch(() => '');
  if (!body) return `Voice TTS failed (${res.status}).`;
  try {
    const parsed = JSON.parse(body) as { message?: string; error?: string };
    return parsed.message || parsed.error || `Voice TTS failed (${res.status}).`;
  } catch {
    return body;
  }
}