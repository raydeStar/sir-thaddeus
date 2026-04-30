// Browser-side voice activity trimming + WAV encode. We decode the raw
// MediaRecorder blob (typically webm/opus), find the speech window with a
// simple RMS threshold over short frames, and re-encode the trimmed range
// as a 16 kHz mono 16-bit PCM WAV. faster-whisper happily accepts WAV and
// short clips transcribe several times faster than the full recording
// because we don't make Whisper chew on leading/trailing silence.

const TARGET_SAMPLE_RATE = 16_000;
// 30 ms frames at 16 kHz = 480 samples. Plenty granular for PTT trimming
// without burning CPU on per-sample work.
const FRAME_SAMPLES = 480;
// Energy threshold expressed as RMS in [0, 1]. Anything below this is
// treated as silence. Set conservatively so quiet speakers aren't clipped.
const SILENCE_RMS = 0.012;
// How much padding to keep around the detected speech window (in frames).
const PAD_FRAMES = 4; // about 120 ms each side

let cachedAudioCtx: AudioContext | null = null;

function getAudioCtx(): AudioContext | null {
  if (cachedAudioCtx) return cachedAudioCtx;
  const Ctor =
    window.AudioContext ||
    (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
  if (!Ctor) return null;
  cachedAudioCtx = new Ctor();
  return cachedAudioCtx;
}

/**
 * Trims silence from a recorded audio blob and returns a 16 kHz mono WAV.
 * On any failure (decode error, unsupported MIME, all-silent buffer) returns
 * the original blob untouched so callers don't have to special-case errors.
 */
export async function trimSilenceToWav(input: Blob): Promise<Blob> {
  if (input.size === 0) return input;
  const ctx = getAudioCtx();
  if (!ctx) return input;
  let buffer: AudioBuffer;
  try {
    const bytes = await input.arrayBuffer();
    buffer = await ctx.decodeAudioData(bytes.slice(0));
  } catch {
    return input;
  }

  const mono = downmixToMono(buffer);
  const resampled = resampleLinear(mono, buffer.sampleRate, TARGET_SAMPLE_RATE);
  const trimmed = trimSilence(resampled);
  if (!trimmed || trimmed.length < TARGET_SAMPLE_RATE / 4) {
    // Less than 250 ms of audio survived, likely all silence or
    // mis-detected. Send the original so Whisper can decide.
    return input;
  }
  const wav = encodeWav16BitMono(trimmed, TARGET_SAMPLE_RATE);
  return new Blob([wav], { type: 'audio/wav' });
}

function downmixToMono(buffer: AudioBuffer): Float32Array {
  if (buffer.numberOfChannels === 1) return buffer.getChannelData(0).slice();
  const length = buffer.length;
  const mixed = new Float32Array(length);
  for (let ch = 0; ch < buffer.numberOfChannels; ch++) {
    const data = buffer.getChannelData(ch);
    for (let i = 0; i < length; i++) mixed[i] += data[i];
  }
  const inv = 1 / buffer.numberOfChannels;
  for (let i = 0; i < length; i++) mixed[i] *= inv;
  return mixed;
}

function resampleLinear(input: Float32Array, fromRate: number, toRate: number): Float32Array {
  if (fromRate === toRate) return input;
  const ratio = fromRate / toRate;
  const outLength = Math.floor(input.length / ratio);
  const out = new Float32Array(outLength);
  for (let i = 0; i < outLength; i++) {
    const srcPos = i * ratio;
    const idx = Math.floor(srcPos);
    const frac = srcPos - idx;
    const a = input[idx] ?? 0;
    const b = input[idx + 1] ?? a;
    out[i] = a + (b - a) * frac;
  }
  return out;
}

function trimSilence(samples: Float32Array): Float32Array | null {
  const frameCount = Math.floor(samples.length / FRAME_SAMPLES);
  if (frameCount === 0) return null;

  let firstVoiced = -1;
  let lastVoiced = -1;
  for (let f = 0; f < frameCount; f++) {
    const start = f * FRAME_SAMPLES;
    let sumSq = 0;
    for (let i = 0; i < FRAME_SAMPLES; i++) {
      const v = samples[start + i];
      sumSq += v * v;
    }
    const rms = Math.sqrt(sumSq / FRAME_SAMPLES);
    if (rms >= SILENCE_RMS) {
      if (firstVoiced === -1) firstVoiced = f;
      lastVoiced = f;
    }
  }
  if (firstVoiced === -1 || lastVoiced === -1) return null;

  const startFrame = Math.max(0, firstVoiced - PAD_FRAMES);
  const endFrame = Math.min(frameCount - 1, lastVoiced + PAD_FRAMES);
  const startSample = startFrame * FRAME_SAMPLES;
  const endSample = Math.min(samples.length, (endFrame + 1) * FRAME_SAMPLES);
  return samples.slice(startSample, endSample);
}

function encodeWav16BitMono(samples: Float32Array, sampleRate: number): ArrayBuffer {
  const dataLength = samples.length * 2;
  const buffer = new ArrayBuffer(44 + dataLength);
  const view = new DataView(buffer);
  let p = 0;
  const writeAscii = (s: string) => { for (let i = 0; i < s.length; i++) view.setUint8(p++, s.charCodeAt(i)); };
  writeAscii('RIFF');
  view.setUint32(p, 36 + dataLength, true); p += 4;
  writeAscii('WAVE');
  writeAscii('fmt ');
  view.setUint32(p, 16, true); p += 4;
  view.setUint16(p, 1, true); p += 2; // PCM
  view.setUint16(p, 1, true); p += 2; // channels
  view.setUint32(p, sampleRate, true); p += 4;
  view.setUint32(p, sampleRate * 2, true); p += 4; // byte rate
  view.setUint16(p, 2, true); p += 2; // block align
  view.setUint16(p, 16, true); p += 2; // bits per sample
  writeAscii('data');
  view.setUint32(p, dataLength, true); p += 4;
  for (let i = 0; i < samples.length; i++) {
    let s = samples[i];
    if (s > 1) s = 1; else if (s < -1) s = -1;
    view.setInt16(p, s < 0 ? s * 0x8000 : s * 0x7fff, true);
    p += 2;
  }
  return buffer;
}
