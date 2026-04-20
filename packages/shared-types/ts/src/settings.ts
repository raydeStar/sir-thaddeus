// AUTO-GENERATED-CANDIDATE — hand-mirrored from packages/shared-types/cs/Settings.cs

export interface LlmSettings {
  provider: string;
  modelId: string;
  baseUrl?: string | null;
  apiKey?: string | null;
}

export interface VoiceSettings {
  sttProvider: string;
  ttsProvider: string;
  piperVoicePath?: string | null;
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

export interface SettingsDocument {
  llm: LlmSettings;
  voice: VoiceSettings;
  shortcuts: ShortcutSettings;
  privacy: PrivacySettings;
}
