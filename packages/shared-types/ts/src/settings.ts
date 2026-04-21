// AUTO-GENERATED-CANDIDATE — hand-mirrored from packages/shared-types/cs/Settings.cs

export interface LlmSettings {
  provider: string;
  modelId: string;
  baseUrl?: string | null;
  apiKey?: string | null;
  maxTokens: number;
  contextWindowTokens: number;
  temperature: number;
  gatekeeperBaseUrl?: string | null;
  gatekeeperModelId?: string | null;
  reusePrimaryForGatekeeperOnSharedEndpoint?: boolean;
}

export interface VoiceSettings {
  sttProvider: string;
  ttsProvider: string;
  piperVoicePath?: string | null;
  ttsVoiceId?: string | null;
  ttsModelId?: string | null;
  sttLanguage?: string | null;
  voiceHostEnabled?: boolean;
  voiceHostBaseUrl?: string | null;
  youtubeAsrProvider?: string | null;
  youtubeAsrModelId?: string | null;
  youtubeLanguageHint?: string | null;
  youtubeDraftTone?: string | null;
  youtubeKeepAudio?: boolean;
}

export interface AudioSettings {
  ttsEnabled: boolean;
  inputGain: number;
  inputDeviceName?: string | null;
  outputDeviceName?: string | null;
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

export interface LocationSettings {
  manualLocation?: string | null;
  use24HourTime: boolean;
  preferredUnits: string;
}

export interface LimitsSettings {
  maxToolCallsPerTurn: number;
  maxToolCallsPerSession: number;
  maxWebPullsPerTurn: number;
  maxFileOpsPerMinute: number;
}

export interface UiPreferencesSettings {
  sendOnEnter: boolean;
  autoSwitchToPermissions: boolean;
  autoConnectOnStartup: boolean;
  autoStartLocalRuntime: boolean;
  minimizeToTrayOnClose: boolean;
}

export type PermissionPolicy = 'off' | 'ask' | 'always';
export type PermissionDeveloperOverride = 'none' | 'off' | 'ask' | 'always';

export interface PermissionsSettings {
  developerOverride: PermissionDeveloperOverride;
  screen: PermissionPolicy;
  files: PermissionPolicy;
  system: PermissionPolicy;
  web: PermissionPolicy;
  memoryRead: PermissionPolicy;
  memoryWrite: PermissionPolicy;
}

export interface FilesSettings {
  allowedRoots: string[];
  disableAllFileAccess: boolean;
  maxDefaultCharsPerRead: number;
}

export interface SettingsDocument {
  llm: LlmSettings;
  voice: VoiceSettings;
  audio: AudioSettings;
  shortcuts: ShortcutSettings;
  privacy: PrivacySettings;
  flags: AppFlags;
  location?: LocationSettings | null;
  limits?: LimitsSettings | null;
  uiPrefs?: UiPreferencesSettings | null;
  permissions?: PermissionsSettings | null;
  files?: FilesSettings | null;
}
