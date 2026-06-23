export type WorkoutIntensity = "none" | "light" | "moderate" | "hard";
export type DataQuality = "mock" | "partial" | "complete" | "missing" | "error";
export type HealthSignalFlag =
  | "sleep_low"
  | "sleep_very_low"
  | "hrv_low"
  | "resting_hr_high"
  | "hard_workout"
  | "protein_low"
  | "stress_high"
  | "mood_low"
  | "soreness_high"
  | "low_activity"
  | "drift_risk"
  | "prior_day_load_high";

export type DayType = "green" | "yellow" | "red" | "blue" | "gold";
export type SignalSeverity = "info" | "watch" | "strong";
export type ReadinessLevel = "strong" | "normal" | "caution" | "recovery";

export interface SleepSummary {
  durationMinutes?: number;
  score?: number;
  startTime?: string;
  endTime?: string;
  quality?: DataQuality;
}

export interface HeartSummary {
  restingHeartRate?: number;
  hrv?: number;
  respiratoryRate?: number;
  spo2?: number;
  quality?: DataQuality;
}

export interface WorkoutSummary {
  durationMinutes?: number;
  intensity?: WorkoutIntensity;
  label?: string;
}

export interface ActivitySummary {
  steps?: number;
  activeMinutes?: number;
  workoutMinutes?: number;
  workoutIntensity?: WorkoutIntensity;
  workouts?: WorkoutSummary[];
  priorDayLoad?: "low" | "normal" | "high";
  quality?: DataQuality;
}

export interface DataQualitySummary {
  provider: string;
  quality: DataQuality;
  generatedAt: string;
  missing: string[];
  warnings: string[];
}

export type DailyHealthSnapshot = {
  date: string;
  provider?: string;
  sleep?: SleepSummary;
  recovery?: HeartSummary;
  heart?: HeartSummary;
  activity?: ActivitySummary;
  nutrition?: {
    caloriesEstimate?: number;
    proteinEstimate?: number;
    hydrationEstimate?: number;
    caffeineAfterNoon?: boolean;
    notes?: string;
  };
  subjective?: {
    mood?: string;
    energy?: number;
    stress?: number;
    soreness?: number;
    focus?: number;
    notes?: string;
  };
  dataQuality?: DataQualitySummary;
};

export interface HealthBaseline {
  date: string;
  sleepDuration7DayAverage?: number;
  sleepDuration14DayAverage?: number;
  restingHeartRate14DayAverage?: number;
  hrv14DayAverage?: number;
  steps7DayAverage?: number;
  energy7DayAverage?: number;
  stress7DayAverage?: number;
  sampleCounts: Record<string, number>;
}

export interface HealthSignalComparison {
  flag: HealthSignalFlag;
  label: string;
  severity: SignalSeverity;
  confidence: number;
  actual?: number | string | boolean;
  baseline?: number;
  explanation: string;
}

export interface BaselineComparison {
  metric: string;
  actual?: number;
  baseline?: number;
  delta?: number;
  direction: "above" | "below" | "same" | "unknown";
  unit?: string;
}

export interface StrategyContributor {
  label: string;
  confidence: number;
  reason: string;
}

export interface SimilarPastDay {
  date: string;
  dayType: DayType;
  overlappingFlags: HealthSignalFlag[];
  summary: string;
}

export interface StrategyBrief {
  date: string;
  readinessLevel: ReadinessLevel;
  dayType: DayType;
  keySignals: HealthSignalComparison[];
  baselineComparisons: BaselineComparison[];
  similarDayPatterns: string;
  likelyContributors: StrategyContributor[];
  bodySignals: HealthSignalComparison[];
  subjectiveSignals: HealthSignalComparison[];
  whatYouMightExpectToday: string;
  recommendations: string[];
  nonNegotiable: string;
  similarPastDaySummary?: string;
  similarPastDays: SimilarPastDay[];
  caveats: string[];
  disclaimer: string;
}

export type MorningStrategyBrief = StrategyBrief;

export interface PatternEpisode {
  date: string;
  flags: HealthSignalFlag[];
  dayType: DayType;
  summary: string;
  after?: {
    energy?: number;
    focus?: number;
    mood?: string;
    notes?: string;
  };
  recommendationsHelped?: boolean;
  repeatedImprovement?: string;
}

export interface ManualHealthCheckin {
  id: string;
  date: string;
  nutrition?: DailyHealthSnapshot["nutrition"];
  subjective?: DailyHealthSnapshot["subjective"];
  notes?: string;
  createdAt: string;
}
