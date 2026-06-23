import {
  HealthBaseline,
  ManualHealthCheckin,
  PatternEpisode,
  StrategyBrief,
  DailyHealthSnapshot
} from "../models.js";

export interface HealthStore {
  saveDailySnapshot(snapshot: DailyHealthSnapshot): Promise<DailyHealthSnapshot>;
  getDailySnapshot(date: string): Promise<DailyHealthSnapshot | undefined>;
  listDailySnapshots(): Promise<DailyHealthSnapshot[]>;
  saveBaseline(baseline: HealthBaseline): Promise<HealthBaseline>;
  getBaseline(date: string): Promise<HealthBaseline | undefined>;
  saveStrategyBrief(brief: StrategyBrief): Promise<StrategyBrief>;
  getStrategyBrief(date: string): Promise<StrategyBrief | undefined>;
  savePatternEpisode(episode: PatternEpisode): Promise<PatternEpisode>;
  listPatternEpisodes(): Promise<PatternEpisode[]>;
  saveManualCheckin(checkin: ManualHealthCheckin): Promise<ManualHealthCheckin>;
  listManualCheckins(date?: string): Promise<ManualHealthCheckin[]>;
}
