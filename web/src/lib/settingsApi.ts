import { runtimeFetch, readRuntimeMetadata } from './runtime';
import type { SettingsDocument } from '@thaddeus/shared-types';

function token(): string {
  return readRuntimeMetadata().token;
}

async function asJson<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`runtime ${res.status}: ${body || res.statusText}`);
  }
  return (await res.json()) as T;
}

export async function getSettings(): Promise<SettingsDocument> {
  const res = await runtimeFetch(token(), '/api/settings');
  return asJson<SettingsDocument>(res);
}

export async function putSettings(doc: SettingsDocument): Promise<SettingsDocument> {
  const res = await runtimeFetch(token(), '/api/settings', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(doc),
  });
  return asJson<SettingsDocument>(res);
}

export interface TestLlmResponse {
  ok: boolean;
  message: string;
  models: string[];
}

export async function testLlm(input: { baseUrl?: string; apiKey?: string }): Promise<TestLlmResponse> {
  const res = await runtimeFetch(token(), '/api/settings/test-llm', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<TestLlmResponse>(res);
}

export interface GatekeeperStatusResponse {
  configured: boolean;
  ok: boolean;
  modelId: string | null;
  baseUrl: string | null;
  reusingPrimary: boolean;
  message: string;
}

export async function getGatekeeperStatus(): Promise<GatekeeperStatusResponse> {
  const res = await runtimeFetch(token(), '/api/settings/gatekeeper-status');
  return asJson<GatekeeperStatusResponse>(res);
}

export interface AudioDeviceInfo {
  deviceNumber: number;
  productName: string;
  displayName: string;
}

export interface AudioDevicesResponse {
  inputs: AudioDeviceInfo[];
  outputs: AudioDeviceInfo[];
}

export async function getAudioDevices(): Promise<AudioDevicesResponse> {
  const res = await runtimeFetch(token(), '/api/audio/devices');
  return asJson<AudioDevicesResponse>(res);
}

export interface PiperVoiceEntry {
  voiceId: string;
  displayName: string;
  gender: string;
  quality: string;
  isInstalled: boolean;
}

export interface PiperVoicesResponse {
  voices: PiperVoiceEntry[];
}

export async function getPiperVoices(): Promise<PiperVoicesResponse> {
  const res = await runtimeFetch(token(), '/api/voice/piper-voices');
  return asJson<PiperVoicesResponse>(res);
}

export interface VoiceHostHealthResponse {
  ok: boolean;
  message: string;
  body?: string | null;
  elapsedMs: number;
}

export async function checkVoiceHostHealth(): Promise<VoiceHostHealthResponse> {
  const res = await runtimeFetch(token(), '/api/voice/host-health');
  return asJson<VoiceHostHealthResponse>(res);
}

export interface RuntimeInfo {
  version: string;
  port: number;
  pid: number;
  startedAt: string;
  uptimeMs: number;
  lockFilePath: string;
  parentPid: number | null;
  managedByShell: boolean;
  testMode: boolean;
}

export async function getRuntimeInfo(): Promise<RuntimeInfo> {
  const res = await runtimeFetch(token(), '/api/runtime-info');
  return asJson<RuntimeInfo>(res);
}

export async function stopRuntime(): Promise<void> {
  const res = await runtimeFetch(token(), '/api/runtime/stop', { method: 'POST' });
  if (!res.ok && res.status !== 202) {
    throw new Error(`runtime ${res.status}: ${await res.text().catch(() => res.statusText)}`);
  }
}
