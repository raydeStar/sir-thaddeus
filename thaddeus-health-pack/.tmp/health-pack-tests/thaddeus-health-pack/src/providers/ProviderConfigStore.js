import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname } from "node:path";
export class FileProviderConfigStore {
    filePath;
    defaults;
    cached;
    constructor(filePath, defaults = {}) {
        this.filePath = filePath;
        this.defaults = defaults;
    }
    async get() {
        if (this.cached) {
            return this.cached;
        }
        try {
            const raw = await readFile(this.filePath, "utf8");
            this.cached = normalizeConfig({
                ...this.defaults,
                ...JSON.parse(raw)
            });
        }
        catch (error) {
            if (!isMissingFileError(error)) {
                throw error;
            }
            this.cached = normalizeConfig(this.defaults);
        }
        return this.cached;
    }
    async replace(document) {
        const normalized = normalizeConfig(document);
        await mkdir(dirname(this.filePath), { recursive: true });
        await writeFile(this.filePath, JSON.stringify(normalized, null, 2), "utf8");
        this.cached = normalized;
        return normalized;
    }
    async clear() {
        return this.replace(normalizeConfig({ selectedProvider: "mock" }));
    }
}
export class InMemoryProviderConfigStore {
    document;
    constructor(initial = {}) {
        this.document = normalizeConfig(initial);
    }
    get() {
        return Promise.resolve(this.document);
    }
    replace(document) {
        this.document = normalizeConfig(document);
        return Promise.resolve(this.document);
    }
    clear() {
        this.document = normalizeConfig({ selectedProvider: "mock" });
        return Promise.resolve(this.document);
    }
}
export function normalizeConfig(input) {
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
export function defaultGoogleScopes() {
    return [
        "https://www.googleapis.com/auth/fitness.sleep.read",
        "https://www.googleapis.com/auth/fitness.heart_rate.read",
        "https://www.googleapis.com/auth/fitness.activity.read"
    ];
}
function normalizeScopes(scopes) {
    const seen = new Set();
    const incoming = scopes && scopes.length > 0 ? scopes : defaultGoogleScopes();
    for (const scope of incoming) {
        const normalized = normalizeGoogleScope(cleanString(scope));
        if (normalized) {
            seen.add(normalized);
        }
    }
    return [...seen];
}
function normalizeGoogleScope(scope) {
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
function cleanString(value) {
    return typeof value === "string" && value.trim().length > 0 ? value.trim() : undefined;
}
function isMissingFileError(error) {
    return typeof error === "object" &&
        error !== null &&
        "code" in error &&
        error.code === "ENOENT";
}
