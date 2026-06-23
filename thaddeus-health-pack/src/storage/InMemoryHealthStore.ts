import {
  DailyHealthSnapshot,
  HealthBaseline,
  ManualHealthCheckin,
  PatternEpisode,
  StrategyBrief
} from "../models.js";
import { HealthStore } from "./HealthStore.js";

export class InMemoryHealthStore implements HealthStore {
  private readonly dailyHealthSnapshots = new Map<string, DailyHealthSnapshot>();
  private readonly dailyHealthBaselines = new Map<string, HealthBaseline>();
  private readonly dailyStrategyBriefs = new Map<string, StrategyBrief>();
  private readonly patternEpisodes = new Map<string, PatternEpisode>();
  private readonly manualHealthCheckins = new Map<string, ManualHealthCheckin>();

  async saveDailySnapshot(snapshot: DailyHealthSnapshot): Promise<DailyHealthSnapshot> {
    this.dailyHealthSnapshots.set(snapshot.date, snapshot);
    return snapshot;
  }

  async getDailySnapshot(date: string): Promise<DailyHealthSnapshot | undefined> {
    return this.dailyHealthSnapshots.get(date);
  }

  async listDailySnapshots(): Promise<DailyHealthSnapshot[]> {
    return [...this.dailyHealthSnapshots.values()].sort((a, b) => a.date.localeCompare(b.date));
  }

  async saveBaseline(baseline: HealthBaseline): Promise<HealthBaseline> {
    this.dailyHealthBaselines.set(baseline.date, baseline);
    return baseline;
  }

  async getBaseline(date: string): Promise<HealthBaseline | undefined> {
    return this.dailyHealthBaselines.get(date);
  }

  async saveStrategyBrief(brief: StrategyBrief): Promise<StrategyBrief> {
    this.dailyStrategyBriefs.set(brief.date, brief);
    return brief;
  }

  async getStrategyBrief(date: string): Promise<StrategyBrief | undefined> {
    return this.dailyStrategyBriefs.get(date);
  }

  async savePatternEpisode(episode: PatternEpisode): Promise<PatternEpisode> {
    this.patternEpisodes.set(episode.date, episode);
    return episode;
  }

  async listPatternEpisodes(): Promise<PatternEpisode[]> {
    return [...this.patternEpisodes.values()].sort((a, b) => a.date.localeCompare(b.date));
  }

  async saveManualCheckin(checkin: ManualHealthCheckin): Promise<ManualHealthCheckin> {
    this.manualHealthCheckins.set(checkin.id, checkin);
    return checkin;
  }

  async listManualCheckins(date?: string): Promise<ManualHealthCheckin[]> {
    const checkins = [...this.manualHealthCheckins.values()];
    return (date ? checkins.filter((checkin) => checkin.date === date) : checkins)
      .sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  }
}
