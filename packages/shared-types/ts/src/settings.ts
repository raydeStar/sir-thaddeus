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
  gatekeeperEnabled?: boolean;
  codexCliPath?: string | null;
  codexReasoningEffort?: string;
}

export interface VoiceSettings {
  sttProvider: string;
  ttsProvider: string;
  piperVoicePath?: string | null;
  ttsVoiceId?: string | null;
  ttsModelId?: string | null;
  sttModelId?: string | null;
  sttLanguage?: string | null;
  voiceHostEnabled?: boolean;
  voiceHostBaseUrl?: string | null;
  youtubeAsrProvider?: string | null;
  youtubeAsrModelId?: string | null;
  youtubeLanguageHint?: string | null;
  youtubeDraftTone?: string | null;
  youtubeKeepAudio?: boolean;
  voiceHostStartupTimeoutMs?: number;
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
  offlineMode?: boolean;
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
  /**
   * Per-tool policy overrides keyed by canonical snake_case tool name.
   * Absent key (or absent/null map) = the tool inherits its group policy.
   */
  toolOverrides?: Record<string, PermissionPolicy> | null;
}

export interface FilesSettings {
  allowedRoots: string[];
  disableAllFileAccess: boolean;
  maxDefaultCharsPerRead: number;
}

export interface RuntimeMemorySettings {
  enabled: boolean;
}

export type ModelCapabilityMode = 'auto' | 'on' | 'off';
export type ModelCapabilityStatus = 'certified' | 'limited' | 'unsupported' | 'error';

export interface ModelCapabilityProbeResult {
  id: string;
  passed: boolean;
  reason: string;
}

export interface ModelCapabilityCertificate {
  capability: string;
  status: ModelCapabilityStatus;
  configurationFingerprint: string;
  configuredModelId: string;
  reportedModelId?: string | null;
  probeVersion: string;
  modelCalls: number;
  elapsedMilliseconds: number;
  testedAt: string;
  probes: ModelCapabilityProbeResult[];
  selectedMode?: 'required' | 'auto' | null;
}

export interface ModelCapabilitySettings {
  wikiWriteMode: ModelCapabilityMode;
  wikiWriteCertificates?: ModelCapabilityCertificate[] | null;
  preferences?: ModelCapabilityPreference[] | null;
  certificates?: ModelCapabilityCertificate[] | null;
}

export interface ModelCapabilityPreference {
  capability: string;
  mode: ModelCapabilityMode;
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
  memory?: RuntimeMemorySettings | null;
  modelCapabilities?: ModelCapabilitySettings | null;
}
