import "dotenv/config";
import { dirname, join } from "node:path";
import { HealthProvider } from "./providers/HealthProvider.js";
import { GoogleHealthProvider } from "./providers/GoogleHealthProvider.js";
import { MockHealthDataProvider } from "./providers/MockHealthDataProvider.js";
import {
  FileProviderConfigStore,
  HealthProviderName,
  ProviderConfigDocument,
  ProviderConfigStore,
  defaultGoogleScopes,
  normalizeConfig
} from "./providers/ProviderConfigStore.js";
import { FileTokenStore, TokenStore } from "./providers/TokenStore.js";

export interface HealthPackConfig {
  providerName: HealthProviderName;
  storePath: string;
  providerConfigPath: string;
  tokenStorePath: string;
  auditPath: string;
  googleHealth: {
    clientId?: string;
    clientSecret?: string;
    redirectUri?: string;
    accessToken?: string;
    refreshToken?: string;
    apiBaseUrl?: string;
    scopes: string[];
  };
}

export function loadHealthPackConfig(env: NodeJS.ProcessEnv = process.env): HealthPackConfig {
  const providerName = env.HEALTH_DATA_PROVIDER === "google-health" ? "google-health" : "mock";
  const storePath = env.HEALTH_STORE_PATH ?? "./data/health-store.json";
  const dataDir = dirname(storePath);

  return {
    providerName,
    storePath,
    providerConfigPath: env.HEALTH_PROVIDER_CONFIG_PATH ?? join(dataDir, "provider-config.json"),
    tokenStorePath: env.HEALTH_TOKEN_STORE_PATH ?? join(dataDir, "provider-tokens.local.json"),
    auditPath: env.HEALTH_AUDIT_PATH ?? join(dataDir, "health-audit.jsonl"),
    googleHealth: {
      clientId: env.GOOGLE_HEALTH_CLIENT_ID,
      clientSecret: env.GOOGLE_HEALTH_CLIENT_SECRET,
      redirectUri: env.GOOGLE_HEALTH_REDIRECT_URI,
      accessToken: env.GOOGLE_HEALTH_ACCESS_TOKEN,
      refreshToken: env.GOOGLE_HEALTH_REFRESH_TOKEN,
      apiBaseUrl: env.GOOGLE_HEALTH_API_BASE_URL,
      scopes: parseScopes(env.GOOGLE_HEALTH_SCOPES)
    }
  };
}

export function createProviderConfigStore(config = loadHealthPackConfig()): ProviderConfigStore {
  return new FileProviderConfigStore(config.providerConfigPath, configToProviderDocument(config));
}

export function createTokenStore(config = loadHealthPackConfig()): TokenStore {
  return new FileTokenStore(config.tokenStorePath);
}

export async function createConfiguredProvider(
  config = loadHealthPackConfig(),
  providerConfig?: ProviderConfigDocument,
  tokenStore?: TokenStore
): Promise<HealthProvider> {
  const document = normalizeConfig(providerConfig ?? configToProviderDocument(config));
  if (document.selectedProvider === "google-health") {
    const safe = document.googleHealth;
    return new GoogleHealthProvider({
      clientId: safe.clientId ?? config.googleHealth.clientId,
      clientSecret: config.googleHealth.clientSecret,
      redirectUri: safe.redirectUri ?? config.googleHealth.redirectUri,
      accessToken: config.googleHealth.accessToken,
      refreshToken: config.googleHealth.refreshToken,
      apiBaseUrl: safe.apiBaseUrl ?? config.googleHealth.apiBaseUrl,
      scopes: safe.scopes,
      tokenStore
    });
  }

  return new MockHealthDataProvider();
}

export function configToProviderDocument(config: HealthPackConfig): ProviderConfigDocument {
  return normalizeConfig({
    selectedProvider: config.providerName,
    googleHealth: {
      clientId: config.googleHealth.clientId,
      redirectUri: config.googleHealth.redirectUri,
      apiBaseUrl: config.googleHealth.apiBaseUrl,
      scopes: config.googleHealth.scopes
    }
  });
}

function parseScopes(value: string | undefined): string[] {
  if (!value) {
    return defaultGoogleScopes();
  }
  return value.split(/[,\s]+/).map((scope) => scope.trim()).filter(Boolean);
}
