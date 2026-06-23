import { DailyHealthSnapshot } from "../models.js";

export type ProviderLifecycleStatus =
  | "not_configured"
  | "configured"
  | "auth_required"
  | "auth_in_progress"
  | "connected"
  | "syncing"
  | "error"
  | "revoked";

export interface ProviderCredentialPresence {
  clientId?: boolean;
  clientSecret?: boolean;
  redirectUri?: boolean;
  accessToken?: boolean;
  refreshToken?: boolean;
}

export interface ProviderSyncSummary {
  lastSyncAt?: string;
  lastSyncRange?: {
    startDate: string;
    endDate: string;
  };
  snapshotCount?: number;
  warnings: string[];
  lastError?: string;
}

export interface HealthProviderStatus {
  providerName: string;
  selectedProvider?: string;
  lifecycle: ProviderLifecycleStatus;
  configured: boolean;
  authenticated: boolean;
  connected: boolean;
  mode: "mock" | "oauth" | "unavailable";
  missingConfig: string[];
  credentials: ProviderCredentialPresence;
  scopes: string[];
  warnings: string[];
  errors: string[];
  sync?: ProviderSyncSummary;
}

export interface HealthProvider {
  providerName: string;
  getDailySnapshot(date: string): Promise<DailyHealthSnapshot>;
  getStatus(): Promise<HealthProviderStatus>;
}
