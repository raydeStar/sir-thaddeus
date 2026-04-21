// AUTO-GENERATED-CANDIDATE — hand-mirrored from packages/shared-types/cs/Settings.cs

export interface LlmSettings {
  provider: string;
  modelId: string;
  baseUrl?: string | null;
  apiKey?: string | null;
  maxTokens: number;
  contextWindowTokens: number;
  temperature: number;
}

export interface VoiceSettings {
  sttProvider: string;
  ttsProvider: string;
  piperVoicePath?: string | null;
}

export interface AudioSettings {
  ttsEnabled: boolean;
  inputGain: number;
}

export interface ShortcutSettings {
  pushToTalk: string;
  stopAll: string;
}

export interface PrivacySettings {
  telemetryEnabled: boolean;
  allowScreenCapture: boolean;
  localOnly: boolean;
}

export interface AppFlags {
  onboardingCompleted: boolean;
}

export interface SettingsDocument {
  llm: LlmSettings;
  voice: VoiceSettings;
  audio: AudioSettings;
  shortcuts: ShortcutSettings;
  privacy: PrivacySettings;
  flags: AppFlags;
}
