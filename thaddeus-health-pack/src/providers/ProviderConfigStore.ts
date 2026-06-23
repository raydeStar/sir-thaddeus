import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname } from "node:path";

export type HealthProviderName = "mock" | "google-health";

export interface GoogleProviderSafeConfig {
  clientId?: string;
  redirectUri?: string;
  apiBaseUrl?: string;
  scopes: string[];
}

export interface ProviderSyncState {
  lastSyncAt?: string;
  lastSyncRange?: {
    startDate: string;
    endDate: string;
  };
  snapshotCount?: number;
  warnings: string[];
  lastError?: string;
}

export interface ProviderConfigDocument {
  selectedProvider: HealthProviderName;
  googleHealth: GoogleProviderSafeConfig;
  authState?: {
    provider: HealthProviderName;
    state: string;
    startedAt: string;
  };
  sync: ProviderSyncState;
  updatedAt: string;
}

export interface ProviderConfigStore {
  get(): Promise<ProviderConfigDocument>;
  replace(document: ProviderConfigDocument): Promise<ProviderConfigDocument>;
  clear(): Promise<ProviderConfigDocument>;
}

export class FileProviderConfigStore implements ProviderConfigStore {
  private cached?: ProviderConfigDocument;

  constructor(private readonly filePath: string, private readonly defaults: Partial<ProviderConfigDocument> = {}) {}

  async get(): Promise<ProviderConfigDocument> {
    if (this.cached) {
      return this.cached;
    }

    try {
      const raw = await readFile(this.filePath, "utf8");
      this.cached = normalizeConfig({
        ...this.defaults,
        ...JSON.parse(raw) as Partial<ProviderConfigDocument>
      });
    } catch (error) {
      if (!isMissingFileError(error)) {
        throw error;
      }
      this.cached = normalizeConfig(this.defaults);
    }

    return this.cached;
  }

  async replace(document: ProviderConfigDocument): Promise<ProviderConfigDocument> {
    const normalized = normalizeConfig(document);
    await mkdir(dirname(this.filePath), { recursive: true });
    await writeFile(this.filePath, JSON.stringify(normalized, null, 2), "utf8");
    this.cached = normalized;
    return normalized;
  }

  async clear(): Promise<ProviderConfigDocument> {
    return this.replace(normalizeConfig({ selectedProvider: "mock" }));
  }
}

export class InMemoryProviderConfigStore implements ProviderConfigStore {
  private document: ProviderConfigDocument;

  constructor(initial: Partial<ProviderConfigDocument> = {}) {
    this.document = normalizeConfig(initial);
  }

  get(): Promise<ProviderConfigDocument> {
    return Promise.resolve(this.document);
  }

  replace(document: ProviderConfigDocument): Promise<ProviderConfigDocument> {
    this.document = normalizeConfig(document);
    return Promise.resolve(this.document);
  }

  clear(): Promise<ProviderConfigDocument> {
    this.document = normalizeConfig({ selectedProvider: "mock" });
    return Promise.resolve(this.document);
  }
}

export function normalizeConfig(input: Partial<ProviderConfigDocument>): ProviderConfigDocument {
  const now = new Date().toISOString();
  const selectedProvider = input.selectedProvider === "google-health" ? "google-health" : "mock";
  const google = input.googleHealth ?? { scopes: [] };
  return {
    selectedProvider,
    googleHealth: {
      clientId: cleanString(google.clientId),
      redirectUri: cleanString(google.redirectUri),
      apiBaseUrl: cleanString(google.apiBaseUrl),
      scopes: normalizeScopes(google.scopes)
    },
    authState: input.authState && input.authState.provider === "google-health"
      ? {
          provider: "google-health",
          state: input.authState.state,
          startedAt: input.authState.startedAt
        }
      : undefined,
    sync: {
      lastSyncAt: input.sync?.lastSyncAt,
      lastSyncRange: input.sync?.lastSyncRange,
      snapshotCount: input.sync?.snapshotCount,
      warnings: [...(input.sync?.warnings ?? [])],
      lastError: cleanString(input.sync?.lastError)
    },
    updatedAt: input.updatedAt ?? now
  };
}

export function defaultGoogleScopes(): string[] {
  return [
    "https://www.googleapis.com/auth/fitness.sleep.read",
    "https://www.googleapis.com/auth/fitness.heart_rate.read",
    "https://www.googleapis.com/auth/fitness.activity.read"
  ];
}

function normalizeScopes(scopes: readonly string[] | undefined): string[] {
  const seen = new Set<string>();
  const incoming = scopes && scopes.length > 0 ? scopes : defaultGoogleScopes();
  for (const scope of incoming) {
    const normalized = normalizeGoogleScope(cleanString(scope));
    if (normalized) {
      seen.add(normalized);
    }
  }
  return [...seen];
}

function normalizeGoogleScope(scope: string | undefined): string | undefined {
  switch (scope) {
    case "sleep.read":
      return "https://www.googleapis.com/auth/fitness.sleep.read";
    case "heart_rate.read":
      return "https://www.googleapis.com/auth/fitness.heart_rate.read";
    case "activity.read":
      return "https://www.googleapis.com/auth/fitness.activity.read";
    case "hrv.read":
      return undefined;
    default:
      return scope;
  }
}

function cleanString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0 ? value.trim() : undefined;
}

function isMissingFileError(error: unknown): boolean {
  return typeof error === "object" &&
    error !== null &&
    "code" in error &&
    (error as { code?: string }).code === "ENOENT";
}
