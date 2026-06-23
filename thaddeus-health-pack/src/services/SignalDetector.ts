import {
  DailyHealthSnapshot,
  DayType,
  HealthBaseline,
  HealthSignalComparison,
  HealthSignalFlag
} from "../models.js";

export class SignalDetector {
  detect(snapshot: DailyHealthSnapshot, baseline: HealthBaseline): HealthSignalComparison[] {
    const signals: HealthSignalComparison[] = [];
    const sleep = snapshot.sleep?.durationMinutes;
    const sleepBase = baseline.sleepDuration14DayAverage;
    const hrv = snapshot.recovery?.hrv;
    const hrvBase = baseline.hrv14DayAverage;
    const rhr = snapshot.recovery?.restingHeartRate;
    const rhrBase = baseline.restingHeartRate14DayAverage;
    const steps = snapshot.activity?.steps;
    const stepsBase = baseline.steps7DayAverage;
    const stress = snapshot.subjective?.stress;
    const stressBase = baseline.stress7DayAverage;

    if (sleep !== undefined && (sleep < 300 || (sleepBase !== undefined && sleep <= sleepBase - 120))) {
      signals.push(signal("sleep_very_low", "Very low sleep", "strong", 0.9, sleep, sleepBase, "Sleep is far below your recent pattern."));
    } else if (sleep !== undefined && (sleep < 390 || (sleepBase !== undefined && sleep <= sleepBase - 60))) {
      signals.push(signal("sleep_low", "Low sleep", "watch", 0.78, sleep, sleepBase, "Sleep is meaningfully below your recent baseline."));
    }

    if (hrv !== undefined && hrvBase !== undefined && hrv <= hrvBase * 0.82) {
      signals.push(signal("hrv_low", "HRV below baseline", "watch", 0.72, hrv, hrvBase, "HRV is lower than your recent average."));
    }

    if (rhr !== undefined && rhrBase !== undefined && rhr >= rhrBase + 6) {
      signals.push(signal("resting_hr_high", "Resting heart rate elevated", "watch", 0.74, rhr, rhrBase, "Resting heart rate is elevated relative to your baseline."));
    }

    if (snapshot.activity?.workoutIntensity === "hard") {
      signals.push(signal("hard_workout", "Recent hard workout", "info", 0.7, "hard", undefined, "A hard workout may add recovery debt or soreness."));
    }

    if (snapshot.activity?.priorDayLoad === "high" || (snapshot.activity?.workoutIntensity === "hard" && (snapshot.activity.workoutMinutes ?? 0) >= 45)) {
      signals.push(signal("prior_day_load_high", "Prior-day load high", "watch", 0.75, snapshot.activity?.priorDayLoad ?? "hard", undefined, "Prior-day training or movement load looks high enough to affect readiness."));
    }

    if (snapshot.nutrition?.proteinEstimate !== undefined && snapshot.nutrition.proteinEstimate < 75) {
      signals.push(signal("protein_low", "Protein looks low", "watch", 0.65, snapshot.nutrition.proteinEstimate, undefined, "Protein appears low for a recovery-supporting day."));
    }

    if (stress !== undefined && (stress >= 8 || (stressBase !== undefined && stress >= stressBase + 2))) {
      signals.push(signal("stress_high", "Stress load high", "strong", 0.82, stress, stressBase, "Subjective stress is elevated versus your recent pattern."));
    }

    if (snapshot.subjective?.mood && ["low", "sad", "flat"].includes(snapshot.subjective.mood.toLowerCase())) {
      signals.push(signal("mood_low", "Mood load", "watch", 0.7, snapshot.subjective.mood, undefined, "Mood check-in suggests extra emotional load."));
    }

    if (snapshot.subjective?.soreness !== undefined && snapshot.subjective.soreness >= 7) {
      signals.push(signal("soreness_high", "Soreness high", "watch", 0.76, snapshot.subjective.soreness, undefined, "Soreness is high enough to shape training and workload."));
    }

    if (steps !== undefined && (steps < 3500 || (stepsBase !== undefined && steps <= stepsBase * 0.55))) {
      signals.push(signal("low_activity", "Low activity", "info", 0.62, steps, stepsBase, "Movement is below your recent pattern."));
    }

    if (signals.some((s) => s.flag === "sleep_low" || s.flag === "sleep_very_low") && signals.some((s) => s.flag === "stress_high" || s.flag === "low_activity")) {
      signals.push(signal("drift_risk", "Drift risk", "watch", 0.68, true, undefined, "Low recovery plus low structure can make the day drift unless it has a simple anchor."));
    }

    return signals;
  }

  classifyDay(signals: HealthSignalComparison[], snapshot: DailyHealthSnapshot, baseline: HealthBaseline): DayType {
    const flags = new Set(signals.map((signal) => signal.flag));
    const bodyFlags = ["sleep_low", "sleep_very_low", "hrv_low", "resting_hr_high", "hard_workout", "soreness_high"];
    const moodFlags = ["stress_high", "mood_low"];
    const strongCount = signals.filter((signal) => signal.severity === "strong").length;
    const warningCount = signals.filter((signal) => signal.severity !== "info").length;

    if (
      snapshot.sleep?.durationMinutes !== undefined &&
      baseline.sleepDuration14DayAverage !== undefined &&
      snapshot.sleep.durationMinutes >= baseline.sleepDuration14DayAverage + 45 &&
      snapshot.recovery?.hrv !== undefined &&
      baseline.hrv14DayAverage !== undefined &&
      snapshot.recovery.hrv >= baseline.hrv14DayAverage * 1.1 &&
      warningCount === 0
    ) {
      return "gold";
    }

    if (flags.has("sleep_very_low") || strongCount >= 2 || warningCount >= 4) {
      return "red";
    }

    if (signals.some((signal) => moodFlags.includes(signal.flag)) && !signals.some((signal) => bodyFlags.includes(signal.flag))) {
      return "blue";
    }

    if (warningCount >= 1 || flags.has("drift_risk")) {
      return "yellow";
    }

    return "green";
  }
}

function signal(
  flag: HealthSignalFlag,
  label: string,
  severity: HealthSignalComparison["severity"],
  confidence: number,
  actual: number | string | boolean | undefined,
  baseline: number | undefined,
  explanation: string
): HealthSignalComparison {
  return { flag, label, severity, confidence, actual, baseline, explanation };
}
