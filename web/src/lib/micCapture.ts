// Microphone capture helpers shared by the chat PTT flow and the
// settings Mic Tester. The browser's MediaDevices API addresses
// inputs by an opaque deviceId, but the Sir Thaddeus settings
// document stores the Windows product name (because that's what the
// native winmm enumeration reports). We have to resolve one to the
// other before calling getUserMedia, otherwise the OS default mic
// is captured instead of the device the user actually selected.

import { getSettings } from './settingsApi';

interface MicResolutionCacheEntry {
  savedName: string | null;
  deviceId: string | null;
  hadLabeledInputs: boolean;
  resolvedAt: number;
}

let resolutionCache: MicResolutionCacheEntry | null = null;
const CACHE_TTL_MS = 30_000;

export interface ResolvedMicDevice {
  /** The product name persisted in settings (audio.inputDeviceName). */
  savedName: string | null;
  /** The MediaDeviceInfo.deviceId we matched, if any. */
  deviceId: string | null;
  /** The MediaDeviceInfo.label we matched, if any. */
  matchedLabel: string | null;
  /** True once browser permission exposed real input labels. */
  hadLabeledInputs: boolean;
}

export interface AcquiredMic {
  stream: MediaStream;
  /** Saved product name we attempted to honor. */
  requestedName: string | null;
  /** Track label as reported by the browser (best truth of what is open). */
  resolvedLabel: string | null;
  /** True when we fell back to the system default device. */
  usedDefault: boolean;
}

export function clearMicResolutionCache(): void {
  resolutionCache = null;
}

export async function resolveSavedInputDevice(): Promise<ResolvedMicDevice> {
  const now = Date.now();
  if (resolutionCache && now - resolutionCache.resolvedAt < CACHE_TTL_MS) {
    const matched = resolutionCache.deviceId
      ? await findLabelForDeviceId(resolutionCache.deviceId)
      : null;
    return {
      savedName: resolutionCache.savedName,
      deviceId: resolutionCache.deviceId,
      matchedLabel: matched,
      hadLabeledInputs: resolutionCache.hadLabeledInputs,
    };
  }

  let savedName: string | null = null;
  try {
    const doc = await getSettings();
    const raw = doc?.audio?.inputDeviceName;
    savedName = typeof raw === 'string' && raw.trim().length > 0 ? raw.trim() : null;
  } catch {
    /* ignore — fall through with no preference */
  }

  let deviceId: string | null = null;
  let matchedLabel: string | null = null;
  let hadLabeledInputs = false;

  if (savedName && navigator.mediaDevices?.enumerateDevices) {
    try {
      const devices = await navigator.mediaDevices.enumerateDevices();
      const inputs = devices.filter((d) => d.kind === 'audioinput' && d.label);
      hadLabeledInputs = inputs.length > 0;
      const target = normalizeDeviceName(savedName);
      const exact = inputs.find((d) => normalizeDeviceName(d.label) === target);
      const partial =
        exact ??
        inputs.find((d) => {
          const label = normalizeDeviceName(d.label);
          return label.includes(target) || target.includes(label);
        });
      if (partial) {
        deviceId = partial.deviceId;
        matchedLabel = partial.label;
      }
    } catch {
      /* ignore — fall back to default below */
    }
  }

  resolutionCache = { savedName, deviceId, hadLabeledInputs, resolvedAt: now };
  return { savedName, deviceId, matchedLabel, hadLabeledInputs };
}

function normalizeDeviceName(value: string): string {
  return value
    .toLowerCase()
    .replace(/\b\d+\s*-\s*/g, '')
    .replace(/[^a-z0-9]+/g, ' ')
    .trim();
}

async function findLabelForDeviceId(deviceId: string): Promise<string | null> {
  if (!navigator.mediaDevices?.enumerateDevices) return null;
  try {
    const devices = await navigator.mediaDevices.enumerateDevices();
    const found = devices.find((d) => d.deviceId === deviceId && d.kind === 'audioinput');
    return found?.label ?? null;
  } catch {
    return null;
  }
}

export async function acquireMicStream(): Promise<AcquiredMic> {
  if (!navigator.mediaDevices?.getUserMedia) {
    throw new Error('Microphone capture is not available in this browser shell.');
  }

  let resolved = await resolveSavedInputDevice();
  if (resolved.savedName && !resolved.deviceId && !resolved.hadLabeledInputs) {
    stopMicStream(await navigator.mediaDevices.getUserMedia({ audio: true }));
    clearMicResolutionCache();
    resolved = await resolveSavedInputDevice();
  }

  const { savedName, deviceId, matchedLabel } = resolved;
  let stream: MediaStream;
  let usedDefault = false;

  if (deviceId) {
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: { deviceId: { exact: deviceId } } as MediaTrackConstraints,
      });
    } catch {
      // Some devices reject `exact` constraints; fall back to default
      // rather than failing the whole capture.
      stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      usedDefault = true;
      // Invalidate cache so the next attempt re-resolves.
      clearMicResolutionCache();
    }
  } else {
    stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    usedDefault = savedName !== null; // user wanted something but we couldn't honor it
  }

  const trackLabel = stream.getAudioTracks()[0]?.label ?? matchedLabel ?? null;
  return { stream, requestedName: savedName, resolvedLabel: trackLabel, usedDefault };
}

export function isStreamLive(stream: MediaStream | null): boolean {
  if (!stream) return false;
  const tracks = stream.getAudioTracks();
  if (tracks.length === 0) return false;
  return tracks.some((t) => t.readyState === 'live' && t.enabled);
}

export function stopMicStream(stream: MediaStream | null): void {
  if (!stream) return;
  for (const track of stream.getTracks()) {
    try {
      track.stop();
    } catch {
      /* best effort */
    }
  }
}
