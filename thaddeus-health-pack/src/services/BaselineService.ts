import { DailyHealthSnapshot, HealthBaseline } from "../models.js";
import { HealthStore } from "../storage/HealthStore.js";

export class BaselineService {
  constructor(private readonly store: HealthStore) {}

  async calculate(date: string): Promise<HealthBaseline> {
    const snapshots = (await this.store.listDailySnapshots())
      .filter((snapshot) => snapshot.date < date)
      .sort((a, b) => b.date.localeCompare(a.date));

    const baseline: HealthBaseline = {
      date,
      sleepDuration7DayAverage: average(lastValues(snapshots, 7, (s) => s.sleep?.durationMinutes)),
      sleepDuration14DayAverage: average(lastValues(snapshots, 14, (s) => s.sleep?.durationMinutes)),
      restingHeartRate14DayAverage: average(lastValues(snapshots, 14, (s) => s.recovery?.restingHeartRate)),
      hrv14DayAverage: average(lastValues(snapshots, 14, (s) => s.recovery?.hrv)),
      steps7DayAverage: average(lastValues(snapshots, 7, (s) => s.activity?.steps)),
      energy7DayAverage: average(lastValues(snapshots, 7, (s) => s.subjective?.energy)),
      stress7DayAverage: average(lastValues(snapshots, 7, (s) => s.subjective?.stress)),
      sampleCounts: {
        sleep7: lastValues(snapshots, 7, (s) => s.sleep?.durationMinutes).length,
        sleep14: lastValues(snapshots, 14, (s) => s.sleep?.durationMinutes).length,
        restingHeartRate14: lastValues(snapshots, 14, (s) => s.recovery?.restingHeartRate).length,
        hrv14: lastValues(snapshots, 14, (s) => s.recovery?.hrv).length,
        steps7: lastValues(snapshots, 7, (s) => s.activity?.steps).length,
        energy7: lastValues(snapshots, 7, (s) => s.subjective?.energy).length,
        stress7: lastValues(snapshots, 7, (s) => s.subjective?.stress).length
      }
    };

    return this.store.saveBaseline(baseline);
  }
}

function lastValues(
  snapshots: DailyHealthSnapshot[],
  days: number,
  selector: (snapshot: DailyHealthSnapshot) => number | undefined
): number[] {
  return snapshots.slice(0, days).map(selector).filter((value): value is number => typeof value === "number");
}

function average(values: number[]): number | undefined {
  if (values.length === 0) {
    return undefined;
  }

  return Math.round((values.reduce((sum, value) => sum + value, 0) / values.length) * 10) / 10;
}
