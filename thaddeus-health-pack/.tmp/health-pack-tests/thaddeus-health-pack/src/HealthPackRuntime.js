import { appendFile, mkdir, readFile } from "node:fs/promises";
import { dirname } from "node:path";
import { createConfiguredProvider, loadHealthPackConfig } from "./config.js";
import { GoogleHealthProvider } from "./providers/GoogleHealthProvider.js";
import { MockHealthDataProvider, buildSeededMockHistory } from "./providers/MockHealthDataProvider.js";
import { InMemoryProviderConfigStore, defaultGoogleScopes, normalizeConfig } from "./providers/ProviderConfigStore.js";
import { InMemoryTokenStore } from "./providers/TokenStore.js";
import { BaselineService } from "./services/BaselineService.js";
import { SimilarPastDaysService } from "./services/SimilarPastDaysService.js";
import { StrategyBriefService } from "./services/StrategyBriefService.js";
import { SignalDetector } from "./services/SignalDetector.js";
import { FileHealthStore } from "./storage/FileHealthStore.js";
import { InMemoryHealthStore } from "./storage/InMemoryHealthStore.js";
export class HealthPackRuntime {
    providerOverride;
    providerCache;
    providerConfigStore;
    tokenStore;
    store;
    baselines;
    similarPastDays;
    strategy;
    detector = new SignalDetector();
    today;
    seeded = false;
    seedPromise;
    seedMockHistory;
    auditPath;
    auditEvents = [];
    constructor(options = {}) {
        this.providerOverride = options.provider;
        this.providerConfigStore = options.providerConfigStore ?? new InMemoryProviderConfigStore({
            selectedProvider: options.provider?.providerName === "google-health" ? "google-health" : "mock"
        });
        this.tokenStore = options.tokenStore ?? new InMemoryTokenStore();
        this.store = options.store ?? (options.storagePath ? new FileHealthStore(options.storagePath) : new InMemoryHealthStore());
        this.baselines = new BaselineService(this.store);
        this.similarPastDays = new SimilarPastDaysService(this.store);
        this.strategy = new StrategyBriefService(this.similarPastDays);
        this.today = options.today ?? (() => new Date().toISOString().slice(0, 10));
        this.seedMockHistory = options.seedMockHistory ?? true;
        this.auditPath = options.auditPath;
    }
    get provider() {
        return this.providerCache ?? this.providerOverride ?? new MockHealthDataProvider();
    }
    async seed(date = this.today()) {
        if (this.seeded) {
            return;
        }
        const snapshots = await buildSeededMockHistory(await this.getProvider(), date, 30);
        for (const snapshot of snapshots) {
            await this.store.saveDailySnapshot(snapshot);
            const baseline = await this.baselines.calculate(snapshot.date);
            const signals = this.detector.detect(snapshot, baseline);
            const dayType = this.detector.classifyDay(signals, snapshot, baseline);
            await this.store.savePatternEpisode({
                date: snapshot.date,
                flags: signals.map((signal) => signal.flag),
                dayType,
                summary: `${dayType} day from seeded mock history.`,
                after: {
                    energy: snapshot.subjective?.energy,
                    focus: snapshot.subjective?.focus,
                    mood: snapshot.subjective?.mood
                },
                recommendationsHelped: (snapshot.subjective?.energy ?? 0) >= 6,
                repeatedImprovement: "Earlier protein, water, and one focused block tended to help seeded days."
            });
        }
        this.seeded = true;
    }
    async getDailySnapshot(date = this.today()) {
        await this.ensureSeeded(date);
        return (await this.store.getDailySnapshot(date)) ?? this.refreshDailySnapshot(date);
    }
    async refreshDailySnapshot(date = this.today()) {
        await this.ensureSeeded(date);
        const existing = await this.store.getDailySnapshot(date);
        const providerSnapshot = await (await this.getProvider()).getDailySnapshot(date);
        const snapshot = mergeSnapshots(existing ?? { date }, providerSnapshot);
        return this.store.saveDailySnapshot(snapshot);
    }
    async syncRange(startDate, endDate) {
        await this.recordAudit("health.sync_started", "ok", { startDate, endDate });
        const dates = rangeDates(startDate, endDate);
        const stored = [];
        const warnings = [];
        try {
            const provider = await this.getProvider();
            for (const date of dates) {
                const snapshot = await provider.getDailySnapshot(date);
                await this.store.saveDailySnapshot(snapshot);
                stored.push(date);
                warnings.push(...(snapshot.dataQuality?.warnings ?? []));
            }
            await this.updateSyncState(startDate, endDate, unique(warnings), undefined);
            await this.recordAudit("health.sync_completed", "ok", {
                startDate,
                endDate,
                snapshotsStored: stored.length,
                warnings: unique(warnings)
            });
            return {
                startDate,
                endDate,
                snapshotsStored: stored.length,
                dates: stored,
                warnings: unique(warnings)
            };
        }
        catch (error) {
            const message = sanitizeText(error instanceof Error ? error.message : String(error));
            await this.updateSyncState(startDate, endDate, unique(warnings), message);
            await this.recordAudit("health.sync_failed", "error", { startDate, endDate, message });
            throw error;
        }
    }
    async backfill(days = 30, throughDate = this.today()) {
        await this.recordAudit("health.backfill_started", "ok", { days, throughDate });
        const dates = trailingDates(throughDate, days);
        const result = await this.syncRange(dates[0], dates[dates.length - 1]);
        await this.recordAudit("health.backfill_completed", "ok", {
            days,
            throughDate,
            snapshotsStored: result.snapshotsStored
        });
        return {
            daysRequested: days,
            snapshotsStored: result.snapshotsStored,
            dates: result.dates,
            warnings: result.warnings
        };
    }
    async getProviderStatus() {
        const provider = await this.getProvider();
        const status = redactProviderStatus(await provider.getStatus());
        const config = await this.providerConfigStore.get();
        const snapshots = await this.store.listDailySnapshots();
        return {
            ...status,
            selectedProvider: config.selectedProvider,
            sync: {
                ...config.sync,
                snapshotCount: snapshots.length,
                warnings: [...(config.sync.warnings ?? [])]
            }
        };
    }
    async getProviderConfigSchema() {
        return {
            providers: ["mock", "google-health"],
            selectedProvider: {
                type: "string",
                enum: ["mock", "google-health"]
            },
            googleHealth: {
                clientId: { type: "string", secret: false },
                clientSecret: { type: "string", secret: true, storage: "token_store", optional: true },
                redirectUri: { type: "string", secret: false },
                accessToken: { type: "string", secret: true, storage: "token_store" },
                refreshToken: { type: "string", secret: true, storage: "token_store" },
                apiBaseUrl: { type: "string", secret: false },
                scopes: { type: "array", items: "string", default: defaultGoogleScopes() },
                authFlow: { type: "string", value: "pkce", clientSecretRequired: false }
            },
            secretStore: this.getSecretStoreStatus()
        };
    }
    getSecretStoreStatus() {
        return this.tokenStore.protectionStatus?.() ?? {
            backend: "unavailable",
            localOnly: true,
            userScoped: false,
            requiresUserKey: true,
            message: "This token store does not report its protection backend."
        };
    }
    async getProviderConfig() {
        const config = await this.providerConfigStore.get();
        const presence = await this.tokenStore.presence("google-health");
        return {
            selectedProvider: config.selectedProvider,
            googleHealth: {
                ...config.googleHealth,
                credentials: {
                    clientId: Boolean(config.googleHealth.clientId),
                    clientSecret: presence.clientSecret,
                    redirectUri: Boolean(config.googleHealth.redirectUri),
                    accessToken: presence.accessToken,
                    refreshToken: presence.refreshToken
                }
            },
            sync: config.sync,
            updatedAt: config.updatedAt
        };
    }
    async setProviderConfig(args) {
        const input = readProviderConfigArgs(args);
        const current = await this.providerConfigStore.get();
        const next = normalizeConfig({
            ...current,
            selectedProvider: input.selectedProvider ?? current.selectedProvider,
            googleHealth: {
                ...current.googleHealth,
                ...input.googleHealth,
                scopes: input.googleHealth?.scopes ?? current.googleHealth.scopes
            },
            updatedAt: new Date().toISOString()
        });
        await this.providerConfigStore.replace(next);
        if (input.googleHealth?.clientSecret || input.googleHealth?.accessToken || input.googleHealth?.refreshToken) {
            await this.tokenStore.set("google-health", {
                clientSecret: input.googleHealth.clientSecret,
                accessToken: input.googleHealth.accessToken,
                refreshToken: input.googleHealth.refreshToken
            });
        }
        this.providerCache = undefined;
        await this.recordAudit("health.provider_config_changed", "ok", {
            selectedProvider: next.selectedProvider,
            googleHealth: {
                clientId: Boolean(next.googleHealth.clientId),
                redirectUri: Boolean(next.googleHealth.redirectUri),
                scopes: next.googleHealth.scopes
            }
        });
        return {
            config: await this.getProviderConfig(),
            status: await this.getProviderStatus()
        };
    }
    async clearProviderConfig() {
        await this.providerConfigStore.clear();
        await this.tokenStore.clear("google-health");
        this.providerCache = undefined;
        await this.recordAudit("health.provider_config_changed", "ok", { selectedProvider: "mock", cleared: true });
        return {
            config: await this.getProviderConfig(),
            status: await this.getProviderStatus()
        };
    }
    async startProviderAuth(args = {}) {
        const config = await this.providerConfigStore.get();
        if (config.selectedProvider !== "google-health") {
            return {
                providerName: config.selectedProvider,
                lifecycle: "not_configured",
                message: "Select google-health as the provider before starting OAuth.",
                missingConfig: []
            };
        }
        const provider = await this.getProvider();
        if (!(provider instanceof GoogleHealthProvider)) {
            return {
                providerName: config.selectedProvider,
                lifecycle: "error",
                message: "Google provider is not available.",
                missingConfig: []
            };
        }
        const input = args && typeof args === "object" ? args : {};
        const result = await provider.startAuth(typeof input.state === "string" ? input.state : undefined);
        if (result.state) {
            await this.providerConfigStore.replace(normalizeConfig({
                ...config,
                authState: {
                    provider: "google-health",
                    state: result.state,
                    startedAt: new Date().toISOString()
                },
                updatedAt: new Date().toISOString()
            }));
        }
        await this.recordAudit("health.provider_auth_started", result.authUrl ? "ok" : "error", {
            lifecycle: result.lifecycle,
            missingConfig: result.missingConfig
        });
        return result;
    }
    async completeProviderAuth(args) {
        const input = args && typeof args === "object" ? args : {};
        const code = typeof input.code === "string" ? input.code : "";
        if (!code) {
            return {
                providerName: "google-health",
                lifecycle: "auth_required",
                connected: false,
                message: "OAuth completion requires a code."
            };
        }
        const config = await this.providerConfigStore.get();
        if (config.authState?.state && input.state !== config.authState.state) {
            await this.recordAudit("health.provider_connected", "denied", { message: "OAuth state mismatch." });
            return {
                providerName: "google-health",
                lifecycle: "error",
                connected: false,
                message: "OAuth state mismatch."
            };
        }
        const provider = await this.getProvider();
        if (!(provider instanceof GoogleHealthProvider)) {
            return {
                providerName: "google-health",
                lifecycle: "error",
                connected: false,
                message: "Google provider is not available."
            };
        }
        const result = await provider.completeAuth(code);
        await this.providerConfigStore.replace(normalizeConfig({ ...config, authState: undefined, updatedAt: new Date().toISOString() }));
        await this.recordAudit("health.provider_connected", result.connected ? "ok" : "error", {
            lifecycle: result.lifecycle,
            message: result.message
        });
        return result;
    }
    async disconnectProvider() {
        const existingTokens = await this.tokenStore.get("google-health");
        const provider = await this.getProvider();
        if (provider instanceof GoogleHealthProvider) {
            await provider.disconnect();
        }
        await this.tokenStore.clear("google-health");
        if (existingTokens.clientSecret) {
            await this.tokenStore.set("google-health", { clientSecret: existingTokens.clientSecret });
        }
        this.providerCache = undefined;
        await this.recordAudit("health.provider_disconnected", "ok", { providerName: "google-health" });
        return {
            config: await this.getProviderConfig(),
            status: await this.getProviderStatus()
        };
    }
    async getBaselines(date = this.today()) {
        await this.ensureSeeded(date);
        return this.baselines.calculate(date);
    }
    async getMorningStrategyBrief(date = this.today()) {
        await this.ensureSeeded(date);
        const snapshot = await this.getDailySnapshot(date);
        const baseline = await this.baselines.calculate(date);
        const brief = await this.strategy.create(snapshot, baseline);
        await this.store.saveStrategyBrief(brief);
        await this.store.savePatternEpisode(this.strategy.toPatternEpisode(brief));
        await this.recordAudit("health.brief_generated", "ok", {
            date,
            providerName: snapshot.provider,
            caveats: brief.caveats
        });
        return brief;
    }
    async getSimilarPastDays(date = this.today()) {
        await this.ensureSeeded(date);
        const snapshot = await this.getDailySnapshot(date);
        const baseline = await this.baselines.calculate(date);
        const flags = this.detector.detect(snapshot, baseline).map((signal) => signal.flag);
        return this.similarPastDays.find(date, flags, 5);
    }
    async logManualCheckin(input) {
        await this.ensureSeeded(input.date ?? this.today());
        const date = input.date ?? this.today();
        const checkin = {
            id: `checkin_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`,
            date,
            nutrition: input.nutrition,
            subjective: input.subjective,
            notes: input.notes,
            createdAt: new Date().toISOString()
        };
        await this.store.saveManualCheckin(checkin);
        const existing = await this.getDailySnapshot(date);
        await this.store.saveDailySnapshot(mergeSnapshots(existing, {
            date,
            nutrition: input.nutrition,
            subjective: input.subjective
        }));
        return checkin;
    }
    async getAuditEvents(limit = 50) {
        const boundedLimit = Math.min(100, Math.max(1, Math.floor(limit)));
        if (!this.auditPath) {
            return this.auditEvents.slice(-boundedLimit);
        }
        const byId = new Map();
        for (const event of await this.readPersistedAuditEvents()) {
            byId.set(event.id, event);
        }
        for (const event of this.auditEvents) {
            byId.set(event.id, event);
        }
        return [...byId.values()]
            .sort((left, right) => left.at.localeCompare(right.at))
            .slice(-boundedLimit);
    }
    tools() {
        return {
            "health.get_daily_snapshot": async (args) => this.getDailySnapshot(readDateArg(args)),
            "health.refresh_daily_snapshot": async (args) => this.refreshDailySnapshot(readDateArg(args)),
            "health.get_baselines": async (args) => this.getBaselines(readDateArg(args)),
            "health.get_morning_strategy_brief": async (args) => this.getMorningStrategyBrief(readDateArg(args)),
            "health.get_similar_past_days": async (args) => this.getSimilarPastDays(readDateArg(args)),
            "health.log_manual_checkin": async (args) => this.logManualCheckin(readCheckinArgs(args)),
            "health.provider_status": async () => this.getProviderStatus(),
            "health.provider_config_schema": async () => this.getProviderConfigSchema(),
            "health.secret_store_status": async () => this.getSecretStoreStatus(),
            "health.set_provider_config": async (args) => this.setProviderConfig(args),
            "health.clear_provider_config": async () => this.clearProviderConfig(),
            "health.start_provider_auth": async (args) => this.startProviderAuth(args),
            "health.complete_provider_auth": async (args) => this.completeProviderAuth(args),
            "health.disconnect_provider": async () => this.disconnectProvider(),
            "health.provider_audit_events": async (args) => this.getAuditEvents(readLimitArg(args)),
            "health.sync_range": async (args) => {
                const parsed = readSyncRangeArgs(args);
                return this.syncRange(parsed.startDate, parsed.endDate);
            },
            "health.backfill": async (args) => {
                const parsed = readBackfillArgs(args);
                return this.backfill(parsed.days, parsed.throughDate);
            }
        };
    }
    jobs() {
        return {
            "health.morning_strategy_job": async () => this.getMorningStrategyBrief(this.today())
        };
    }
    async getProvider() {
        if (this.providerOverride) {
            return this.providerOverride;
        }
        if (!this.providerCache) {
            const config = loadHealthPackConfig();
            this.providerCache = await createConfiguredProvider(config, await this.providerConfigStore.get(), this.tokenStore);
        }
        return this.providerCache;
    }
    async ensureSeeded(date) {
        if (!this.seedMockHistory || this.seeded) {
            return;
        }
        this.seedPromise ??= this.seed(date);
        await this.seedPromise;
    }
    async updateSyncState(startDate, endDate, warnings, lastError) {
        const config = await this.providerConfigStore.get();
        const snapshots = await this.store.listDailySnapshots();
        await this.providerConfigStore.replace(normalizeConfig({
            ...config,
            sync: {
                lastSyncAt: new Date().toISOString(),
                lastSyncRange: { startDate, endDate },
                snapshotCount: snapshots.length,
                warnings,
                lastError
            },
            updatedAt: new Date().toISOString()
        }));
    }
    async recordAudit(action, result, details = {}) {
        const event = {
            id: `ha_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`,
            action,
            result,
            at: new Date().toISOString(),
            providerName: typeof details.providerName === "string" ? details.providerName : undefined,
            message: typeof details.message === "string" ? sanitizeText(details.message) : undefined,
            details: sanitizeDetails(details)
        };
        this.auditEvents.push(event);
        if (!this.auditPath) {
            return;
        }
        await mkdir(dirname(this.auditPath), { recursive: true });
        await appendFile(this.auditPath, JSON.stringify(event) + "\n", "utf8");
    }
    async readPersistedAuditEvents() {
        if (!this.auditPath) {
            return [];
        }
        let content = "";
        try {
            content = await readFile(this.auditPath, "utf8");
        }
        catch (error) {
            if (error.code === "ENOENT") {
                return [];
            }
            throw error;
        }
        const events = [];
        for (const line of content.split(/\r?\n/)) {
            if (!line.trim()) {
                continue;
            }
            try {
                const parsed = JSON.parse(line);
                if (typeof parsed.id === "string"
                    && typeof parsed.action === "string"
                    && typeof parsed.result === "string"
                    && typeof parsed.at === "string") {
                    events.push({
                        id: parsed.id,
                        action: parsed.action,
                        result: parsed.result,
                        at: parsed.at,
                        providerName: typeof parsed.providerName === "string" ? parsed.providerName : undefined,
                        message: typeof parsed.message === "string" ? parsed.message : undefined,
                        details: parsed.details && typeof parsed.details === "object"
                            ? sanitizeDetails(parsed.details)
                            : undefined
                    });
                }
            }
            catch {
                // Ignore malformed audit lines so one bad append does not hide newer events.
            }
        }
        return events;
    }
}
function readBackfillArgs(args) {
    if (!args || typeof args !== "object") {
        return { days: 30 };
    }
    const input = args;
    return {
        days: typeof input.days === "number" && Number.isFinite(input.days) ? Math.max(1, Math.floor(input.days)) : 30,
        throughDate: typeof input.throughDate === "string"
            ? input.throughDate
            : typeof input.date === "string"
                ? input.date
                : undefined
    };
}
function readSyncRangeArgs(args) {
    const input = args && typeof args === "object" ? args : {};
    const endDate = typeof input.endDate === "string" ? input.endDate : new Date().toISOString().slice(0, 10);
    const startDate = typeof input.startDate === "string" ? input.startDate : endDate;
    return { startDate, endDate };
}
function readLimitArg(args) {
    if (!args || typeof args !== "object") {
        return 50;
    }
    const input = args;
    return typeof input.limit === "number" && Number.isFinite(input.limit)
        ? Math.min(100, Math.max(1, Math.floor(input.limit)))
        : 50;
}
function trailingDates(throughDate, days) {
    const end = new Date(`${throughDate}T00:00:00Z`);
    const dates = [];
    for (let offset = days - 1; offset >= 0; offset -= 1) {
        const date = new Date(end);
        date.setUTCDate(end.getUTCDate() - offset);
        dates.push(date.toISOString().slice(0, 10));
    }
    return dates;
}
function rangeDates(startDate, endDate) {
    const start = new Date(`${startDate}T00:00:00Z`);
    const end = new Date(`${endDate}T00:00:00Z`);
    if (Number.isNaN(start.getTime()) || Number.isNaN(end.getTime()) || start > end) {
        throw new Error("sync_range requires valid startDate and endDate in YYYY-MM-DD order.");
    }
    const dates = [];
    for (const cursor = new Date(start); cursor <= end; cursor.setUTCDate(cursor.getUTCDate() + 1)) {
        dates.push(cursor.toISOString().slice(0, 10));
    }
    return dates;
}
function redactProviderStatus(status) {
    return {
        providerName: status.providerName,
        selectedProvider: status.selectedProvider,
        lifecycle: status.lifecycle,
        configured: status.configured,
        authenticated: status.authenticated,
        connected: status.connected,
        mode: status.mode,
        missingConfig: [...status.missingConfig],
        credentials: { ...status.credentials },
        scopes: [...status.scopes],
        warnings: status.warnings.map(sanitizeText),
        errors: status.errors.map(sanitizeText),
        sync: status.sync ? {
            ...status.sync,
            warnings: status.sync.warnings.map(sanitizeText),
            lastError: status.sync.lastError ? sanitizeText(status.sync.lastError) : undefined
        } : undefined
    };
}
function readDateArg(args) {
    if (args && typeof args === "object" && "date" in args && typeof args.date === "string") {
        return args.date;
    }
    return undefined;
}
function readCheckinArgs(args) {
    if (!args || typeof args !== "object") {
        return {};
    }
    return args;
}
function readProviderConfigArgs(args) {
    const input = args && typeof args === "object" ? args : {};
    const selectedProvider = input.selectedProvider === "google-health" || input.providerName === "google-health"
        ? "google-health"
        : input.selectedProvider === "mock" || input.providerName === "mock"
            ? "mock"
            : undefined;
    const googleInput = input.googleHealth && typeof input.googleHealth === "object"
        ? input.googleHealth
        : input;
    return {
        selectedProvider,
        googleHealth: {
            clientId: readString(googleInput.clientId),
            clientSecret: readString(googleInput.clientSecret),
            redirectUri: readString(googleInput.redirectUri),
            accessToken: readString(googleInput.accessToken),
            refreshToken: readString(googleInput.refreshToken),
            apiBaseUrl: readString(googleInput.apiBaseUrl),
            scopes: Array.isArray(googleInput.scopes) ? googleInput.scopes.filter((scope) => typeof scope === "string") : undefined
        }
    };
}
function readString(value) {
    return typeof value === "string" && value.trim().length > 0 ? value.trim() : undefined;
}
function mergeSnapshots(existing, incoming) {
    return {
        ...existing,
        ...incoming,
        date: incoming.date ?? existing.date,
        sleep: { ...existing.sleep, ...incoming.sleep },
        recovery: { ...existing.recovery, ...incoming.recovery },
        activity: { ...existing.activity, ...incoming.activity },
        nutrition: { ...existing.nutrition, ...incoming.nutrition },
        subjective: { ...existing.subjective, ...incoming.subjective }
    };
}
function unique(values) {
    return [...new Set(values.filter(Boolean))];
}
function sanitizeDetails(details) {
    return Object.fromEntries(Object.entries(details).map(([key, value]) => [
        key,
        isSecretKey(key) ? "[REDACTED]" : sanitizeValue(value)
    ]));
}
function sanitizeValue(value) {
    if (typeof value === "string") {
        return sanitizeText(value);
    }
    if (Array.isArray(value)) {
        return value.map(sanitizeValue);
    }
    if (value && typeof value === "object") {
        return sanitizeDetails(value);
    }
    return value;
}
function sanitizeText(value) {
    return value
        .replace(/Bearer\s+[A-Za-z0-9._~+/-]+/gi, "Bearer [REDACTED]")
        .replace(/[A-Za-z0-9_+=/-]{40,}/g, "[REDACTED]");
}
function isSecretKey(key) {
    return /token|secret|authorization|password|cookie/i.test(key);
}
