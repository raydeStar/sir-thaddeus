import { mkdir, readFile, writeFile } from "node:fs/promises";
import { createCipheriv, createDecipheriv, randomBytes } from "node:crypto";
import { dirname } from "node:path";
import {
  DailyHealthSnapshot,
  HealthBaseline,
  ManualHealthCheckin,
  PatternEpisode,
  StrategyBrief
} from "../models.js";
import { protectLocalSecret, unprotectLocalSecret } from "../providers/TokenStore.js";
import { HealthStore } from "./HealthStore.js";

interface HealthStoreData {
  daily_health_snapshots: DailyHealthSnapshot[];
  daily_health_baselines: HealthBaseline[];
  daily_strategy_briefs: StrategyBrief[];
  pattern_episodes: PatternEpisode[];
  manual_health_checkins: ManualHealthCheckin[];
}

interface EncryptedHealthStoreDocument {
  format: "sir-thaddeus-health-store-v1";
  encrypted: true;
  algorithm: "aes-256-gcm";
  keyRef: string;
  payload: string;
}

export class FileHealthStore implements HealthStore {
  private data?: HealthStoreData;

  constructor(private readonly filePath: string) {}

  async saveDailySnapshot(snapshot: DailyHealthSnapshot): Promise<DailyHealthSnapshot> {
    const data = await this.load();
    upsertBy(data.daily_health_snapshots, snapshot, (item) => item.date);
    await this.save(data);
    return snapshot;
  }

  async getDailySnapshot(date: string): Promise<DailyHealthSnapshot | undefined> {
    return (await this.load()).daily_health_snapshots.find((snapshot) => snapshot.date === date);
  }

  async listDailySnapshots(): Promise<DailyHealthSnapshot[]> {
    return [...(await this.load()).daily_health_snapshots].sort((a, b) => a.date.localeCompare(b.date));
  }

  async saveBaseline(baseline: HealthBaseline): Promise<HealthBaseline> {
    const data = await this.load();
    upsertBy(data.daily_health_baselines, baseline, (item) => item.date);
    await this.save(data);
    return baseline;
  }

  async getBaseline(date: string): Promise<HealthBaseline | undefined> {
    return (await this.load()).daily_health_baselines.find((baseline) => baseline.date === date);
  }

  async saveStrategyBrief(brief: StrategyBrief): Promise<StrategyBrief> {
    const data = await this.load();
    upsertBy(data.daily_strategy_briefs, brief, (item) => item.date);
    await this.save(data);
    return brief;
  }

  async getStrategyBrief(date: string): Promise<StrategyBrief | undefined> {
    return (await this.load()).daily_strategy_briefs.find((brief) => brief.date === date);
  }

  async savePatternEpisode(episode: PatternEpisode): Promise<PatternEpisode> {
    const data = await this.load();
    upsertBy(data.pattern_episodes, episode, (item) => item.date);
    await this.save(data);
    return episode;
  }

  async listPatternEpisodes(): Promise<PatternEpisode[]> {
    return [...(await this.load()).pattern_episodes].sort((a, b) => a.date.localeCompare(b.date));
  }

  async saveManualCheckin(checkin: ManualHealthCheckin): Promise<ManualHealthCheckin> {
    const data = await this.load();
    upsertBy(data.manual_health_checkins, checkin, (item) => item.id);
    await this.save(data);
    return checkin;
  }

  async listManualCheckins(date?: string): Promise<ManualHealthCheckin[]> {
    const checkins = [...(await this.load()).manual_health_checkins];
    return (date ? checkins.filter((checkin) => checkin.date === date) : checkins)
      .sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  }

  private async load(): Promise<HealthStoreData> {
    if (this.data) {
      return this.data;
    }

    try {
      const raw = await readFile(this.filePath, "utf8");
      this.data = parseStoreDocument(this.filePath, raw);
    } catch (error) {
      if (isMissingFileError(error)) {
        this.data = emptyData();
      } else {
        throw error;
      }
    }

    return this.data;
  }

  private async save(data: HealthStoreData): Promise<void> {
    await mkdir(dirname(this.filePath), { recursive: true });
    await writeFile(this.filePath, serializeStoreDocument(this.filePath, data), { encoding: "utf8", mode: 0o600 });
  }
}

function emptyData(): HealthStoreData {
  return {
    daily_health_snapshots: [],
    daily_health_baselines: [],
    daily_strategy_briefs: [],
    pattern_episodes: [],
    manual_health_checkins: []
  };
}

function upsertBy<T>(items: T[], item: T, keySelector: (item: T) => string): void {
  const key = keySelector(item);
  const index = items.findIndex((existing) => keySelector(existing) === key);
  if (index >= 0) {
    items[index] = item;
  } else {
    items.push(item);
  }
}

function isMissingFileError(error: unknown): boolean {
  return typeof error === "object" &&
    error !== null &&
    "code" in error &&
    (error as { code?: string }).code === "ENOENT";
}

function parseStoreDocument(filePath: string, raw: string): HealthStoreData {
  const parsed = JSON.parse(raw) as Partial<HealthStoreData> | EncryptedHealthStoreDocument;
  if (isEncryptedDocument(parsed)) {
    const key = Buffer.from(unprotectLocalSecret(storeKeyScope(filePath), parsed.keyRef), "base64url");
    const payload = Buffer.from(parsed.payload, "base64url");
    const iv = payload.subarray(0, 12);
    const tag = payload.subarray(12, 28);
    const ciphertext = payload.subarray(28);
    const decipher = createDecipheriv("aes-256-gcm", key, iv);
    decipher.setAuthTag(tag);
    const plaintext = Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString("utf8");
    return normalizeData(JSON.parse(plaintext) as Partial<HealthStoreData>);
  }

  return normalizeData(parsed);
}

function serializeStoreDocument(filePath: string, data: HealthStoreData): string {
  if (process.env.HEALTH_STORE_ENCRYPTION === "plaintext") {
    return JSON.stringify(data, null, 2);
  }

  const key = randomBytes(32);
  const iv = randomBytes(12);
  const cipher = createCipheriv("aes-256-gcm", key, iv);
  const ciphertext = Buffer.concat([cipher.update(JSON.stringify(data), "utf8"), cipher.final()]);
  const payload = Buffer.concat([iv, cipher.getAuthTag(), ciphertext]).toString("base64url");
  const document: EncryptedHealthStoreDocument = {
    format: "sir-thaddeus-health-store-v1",
    encrypted: true,
    algorithm: "aes-256-gcm",
    keyRef: protectLocalSecret(storeKeyScope(filePath), key.toString("base64url")),
    payload
  };
  return JSON.stringify(document, null, 2);
}

function normalizeData(data: Partial<HealthStoreData>): HealthStoreData {
  return { ...emptyData(), ...data };
}

function isEncryptedDocument(value: unknown): value is EncryptedHealthStoreDocument {
  return Boolean(value)
    && typeof value === "object"
    && (value as { format?: unknown }).format === "sir-thaddeus-health-store-v1"
    && (value as { encrypted?: unknown }).encrypted === true;
}

function storeKeyScope(filePath: string): string {
  return `health-store:${filePath}`;
}
