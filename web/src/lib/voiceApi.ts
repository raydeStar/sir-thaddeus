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

export interface SpeechTranscript {
  text: string;
  requestId: string;
}

export interface VoicePttEvent {
  phase: 'down' | 'up' | 'shutup';
  source: string;
  atUtc: string;
}

export interface VoiceWarmupResponse {
  ok: boolean;
  message: string;
  body?: string | null;
  elapsedMs: number;
  voiceHostEnabled: boolean;
  hostReachable: boolean;
  asrReady: boolean;
  ttsReady: boolean;
  inputAvailable: boolean;
  outputAvailable: boolean;
  status: string;
  errorCode?: string | null;
}

export async function warmVoiceHost(): Promise<VoiceWarmupResponse> {
  const res = await runtimeFetch(token(), '/api/voice/warmup', { method: 'POST' });
  if (!res.ok) {
    throw new Error(await readVoiceError(res, 'Voice warmup'));
  }
  return res.json() as Promise<VoiceWarmupResponse>;
}

export async function transcribeSpeech(audio: Blob, sessionId?: string): Promise<SpeechTranscript> {
  const requestId = `chat-asr-${crypto.randomUUID?.() ?? Date.now().toString(36)}`;
  const form = new FormData();
  form.set('audio', audio, `speech.${extensionForAudio(audio.type)}`);
  form.set('requestId', requestId);
  if (sessionId) form.set('sessionId', sessionId);

  const res = await runtimeFetch(token(), '/api/voice/asr', {
    method: 'POST',
    body: form,
  });
  if (!res.ok) {
    throw new Error(await readVoiceError(res, 'Voice ASR'));
  }
  return res.json() as Promise<SpeechTranscript>;
}

export async function subscribeVoicePttEvents(
  onEvent: (evt: VoicePttEvent) => void,
  signal: AbortSignal,
): Promise<void> {
  const res = await runtimeFetch(token(), '/api/voice/ptt/events', { signal });
  if (!res.ok || !res.body) {
    throw new Error(await readVoiceError(res, 'Voice PTT'));
  }

  const reader = res.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = '';
  while (!signal.aborted) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += value;

    let boundary = buffer.indexOf('\n\n');
    while (boundary >= 0) {
      const frame = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);
      const dataLine = frame.split('\n').find((line) => line.startsWith('data:'));
      if (dataLine) {
        try {
          onEvent(JSON.parse(dataLine.slice('data:'.length).trim()) as VoicePttEvent);
        } catch {
          /* ignore malformed event frames */
        }
      }
      boundary = buffer.indexOf('\n\n');
    }
  }
}

function extensionForAudio(contentType: string): string {
  if (contentType.includes('wav')) return 'wav';
  if (contentType.includes('ogg')) return 'ogg';
  if (contentType.includes('mp4')) return 'm4a';
  return 'webm';
}

async function readVoiceError(res: Response, label = 'Voice TTS'): Promise<string> {
  const body = await res.text().catch(() => '');
  if (!body) return `${label} failed (${res.status}).`;
  try {
    const parsed = JSON.parse(body) as { message?: string; error?: string };
    return parsed.message || parsed.error || `${label} failed (${res.status}).`;
  } catch {
    return body;
  }
}