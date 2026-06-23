import { mkdir, readFile, writeFile } from "node:fs/promises";
import { createCipheriv, createDecipheriv, randomBytes } from "node:crypto";
import { dirname } from "node:path";
import { protectLocalSecret, unprotectLocalSecret } from "../providers/TokenStore.js";
export class FileHealthStore {
    filePath;
    data;
    constructor(filePath) {
        this.filePath = filePath;
    }
    async saveDailySnapshot(snapshot) {
        const data = await this.load();
        upsertBy(data.daily_health_snapshots, snapshot, (item) => item.date);
        await this.save(data);
        return snapshot;
    }
    async getDailySnapshot(date) {
        return (await this.load()).daily_health_snapshots.find((snapshot) => snapshot.date === date);
    }
    async listDailySnapshots() {
        return [...(await this.load()).daily_health_snapshots].sort((a, b) => a.date.localeCompare(b.date));
    }
    async saveBaseline(baseline) {
        const data = await this.load();
        upsertBy(data.daily_health_baselines, baseline, (item) => item.date);
        await this.save(data);
        return baseline;
    }
    async getBaseline(date) {
        return (await this.load()).daily_health_baselines.find((baseline) => baseline.date === date);
    }
    async saveStrategyBrief(brief) {
        const data = await this.load();
        upsertBy(data.daily_strategy_briefs, brief, (item) => item.date);
        await this.save(data);
        return brief;
    }
    async getStrategyBrief(date) {
        return (await this.load()).daily_strategy_briefs.find((brief) => brief.date === date);
    }
    async savePatternEpisode(episode) {
        const data = await this.load();
        upsertBy(data.pattern_episodes, episode, (item) => item.date);
        await this.save(data);
        return episode;
    }
    async listPatternEpisodes() {
        return [...(await this.load()).pattern_episodes].sort((a, b) => a.date.localeCompare(b.date));
    }
    async saveManualCheckin(checkin) {
        const data = await this.load();
        upsertBy(data.manual_health_checkins, checkin, (item) => item.id);
        await this.save(data);
        return checkin;
    }
    async listManualCheckins(date) {
        const checkins = [...(await this.load()).manual_health_checkins];
        return (date ? checkins.filter((checkin) => checkin.date === date) : checkins)
            .sort((a, b) => a.createdAt.localeCompare(b.createdAt));
    }
    async load() {
        if (this.data) {
            return this.data;
        }
        try {
            const raw = await readFile(this.filePath, "utf8");
            this.data = parseStoreDocument(this.filePath, raw);
        }
        catch (error) {
            if (isMissingFileError(error)) {
                this.data = emptyData();
            }
            else {
                throw error;
            }
        }
        return this.data;
    }
    async save(data) {
        await mkdir(dirname(this.filePath), { recursive: true });
        await writeFile(this.filePath, serializeStoreDocument(this.filePath, data), { encoding: "utf8", mode: 0o600 });
    }
}
function emptyData() {
    return {
        daily_health_snapshots: [],
        daily_health_baselines: [],
        daily_strategy_briefs: [],
        pattern_episodes: [],
        manual_health_checkins: []
    };
}
function upsertBy(items, item, keySelector) {
    const key = keySelector(item);
    const index = items.findIndex((existing) => keySelector(existing) === key);
    if (index >= 0) {
        items[index] = item;
    }
    else {
        items.push(item);
    }
}
function isMissingFileError(error) {
    return typeof error === "object" &&
        error !== null &&
        "code" in error &&
        error.code === "ENOENT";
}
function parseStoreDocument(filePath, raw) {
    const parsed = JSON.parse(raw);
    if (isEncryptedDocument(parsed)) {
        const key = Buffer.from(unprotectLocalSecret(storeKeyScope(filePath), parsed.keyRef), "base64url");
        const payload = Buffer.from(parsed.payload, "base64url");
        const iv = payload.subarray(0, 12);
        const tag = payload.subarray(12, 28);
        const ciphertext = payload.subarray(28);
        const decipher = createDecipheriv("aes-256-gcm", key, iv);
        decipher.setAuthTag(tag);
        const plaintext = Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString("utf8");
        return normalizeData(JSON.parse(plaintext));
    }
    return normalizeData(parsed);
}
function serializeStoreDocument(filePath, data) {
    if (process.env.HEALTH_STORE_ENCRYPTION === "plaintext") {
        return JSON.stringify(data, null, 2);
    }
    const key = randomBytes(32);
    const iv = randomBytes(12);
    const cipher = createCipheriv("aes-256-gcm", key, iv);
    const ciphertext = Buffer.concat([cipher.update(JSON.stringify(data), "utf8"), cipher.final()]);
    const payload = Buffer.concat([iv, cipher.getAuthTag(), ciphertext]).toString("base64url");
    const document = {
        format: "sir-thaddeus-health-store-v1",
        encrypted: true,
        algorithm: "aes-256-gcm",
        keyRef: protectLocalSecret(storeKeyScope(filePath), key.toString("base64url")),
        payload
    };
    return JSON.stringify(document, null, 2);
}
function normalizeData(data) {
    return { ...emptyData(), ...data };
}
function isEncryptedDocument(value) {
    return Boolean(value)
        && typeof value === "object"
        && value.format === "sir-thaddeus-health-store-v1"
        && value.encrypted === true;
}
function storeKeyScope(filePath) {
    return `health-store:${filePath}`;
}
