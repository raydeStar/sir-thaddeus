import { SignalDetector } from "./SignalDetector.js";
export class StrategyBriefService {
    similarPastDays;
    detector = new SignalDetector();
    constructor(similarPastDays) {
        this.similarPastDays = similarPastDays;
    }
    async create(snapshot, baseline) {
        const signals = this.detector.detect(snapshot, baseline);
        const dayType = this.detector.classifyDay(signals, snapshot, baseline);
        const bodySignals = signals.filter((signal) => ["sleep_low", "sleep_very_low", "hrv_low", "resting_hr_high", "hard_workout", "soreness_high", "low_activity"].includes(signal.flag));
        const subjectiveSignals = signals.filter((signal) => ["stress_high", "mood_low", "protein_low", "drift_risk"].includes(signal.flag));
        const similarPastDays = await this.similarPastDays.find(snapshot.date, signals.map((signal) => signal.flag), 5);
        return {
            date: snapshot.date,
            readinessLevel: readinessLevel(dayType),
            dayType,
            keySignals: signals,
            baselineComparisons: baselineComparisons(snapshot, baseline),
            similarDayPatterns: similarSummary(similarPastDays) ?? "Similar-day pattern matching needs more history.",
            likelyContributors: contributors(signals),
            bodySignals,
            subjectiveSignals,
            whatYouMightExpectToday: expectation(dayType, signals),
            recommendations: recommendations(dayType, signals),
            nonNegotiable: nonNegotiable(dayType, signals),
            similarPastDaySummary: similarSummary(similarPastDays),
            similarPastDays,
            caveats: caveats(snapshot),
            disclaimer: "This is a practical planning brief based on recent patterns. For severe, persistent, or unusual symptoms, use qualified health support."
        };
    }
    toPatternEpisode(brief) {
        const flags = [...brief.bodySignals, ...brief.subjectiveSignals].map((signal) => signal.flag);
        return {
            date: brief.date,
            flags,
            dayType: brief.dayType,
            summary: `${brief.dayType} day with ${flags.length === 0 ? "no major flags" : flags.join(", ")}.`,
            repeatedImprovement: brief.recommendations[0]
        };
    }
}
function contributors(signals) {
    return signals
        .filter((signal) => signal.severity !== "info")
        .sort((a, b) => b.confidence - a.confidence)
        .slice(0, 4)
        .map((signal) => ({
        label: signal.label,
        confidence: signal.confidence,
        reason: signal.explanation
    }));
}
function readinessLevel(dayType) {
    if (dayType === "gold")
        return "strong";
    if (dayType === "red")
        return "recovery";
    if (dayType === "yellow" || dayType === "blue")
        return "caution";
    return "normal";
}
function baselineComparisons(snapshot, baseline) {
    return [
        comparison("sleepDurationMinutes", snapshot.sleep?.durationMinutes, baseline.sleepDuration14DayAverage, "minutes"),
        comparison("restingHeartRate", snapshot.recovery?.restingHeartRate ?? snapshot.heart?.restingHeartRate, baseline.restingHeartRate14DayAverage, "bpm"),
        comparison("hrv", snapshot.recovery?.hrv ?? snapshot.heart?.hrv, baseline.hrv14DayAverage, "ms"),
        comparison("steps", snapshot.activity?.steps, baseline.steps7DayAverage, "steps"),
        comparison("energy", snapshot.subjective?.energy, baseline.energy7DayAverage, "score"),
        comparison("stress", snapshot.subjective?.stress, baseline.stress7DayAverage, "score")
    ];
}
function comparison(metric, actual, baseline, unit) {
    if (actual === undefined || baseline === undefined) {
        return { metric, actual, baseline, direction: "unknown", unit };
    }
    const delta = Math.round((actual - baseline) * 10) / 10;
    return {
        metric,
        actual,
        baseline,
        delta,
        direction: delta > 0 ? "above" : delta < 0 ? "below" : "same",
        unit
    };
}
function expectation(dayType, signals) {
    const flags = new Set(signals.map((signal) => signal.flag));
    if (dayType === "red") {
        return "Expect lower frustration tolerance and slower ramp-up. Keep the day smaller and protect recovery.";
    }
    if (dayType === "blue") {
        return "Your body metrics look mostly workable, but mood or stress may color the day. Use structure and lighter expectations.";
    }
    if (dayType === "gold") {
        return "You may have an unusually good window for demanding work or training, as long as you do not overreach.";
    }
    if (flags.has("drift_risk")) {
        return "The day may drift without an early anchor. A simple first block matters more than a perfect plan.";
    }
    if (dayType === "yellow") {
        return "You can probably still have a solid day, but pushing hard everywhere is likely to backfire.";
    }
    return "No major recovery warnings. Use the day normally and keep the basics steady.";
}
function recommendations(dayType, signals) {
    const flags = new Set(signals.map((signal) => signal.flag));
    const recs = [];
    if (flags.has("protein_low"))
        recs.push("Get protein and water before noon.");
    if (flags.has("sleep_very_low") || flags.has("sleep_low"))
        recs.push("Do one focused work block before side quests.");
    if (flags.has("hard_workout") || flags.has("soreness_high"))
        recs.push("Train moderately today; no PR attempt.");
    if (flags.has("prior_day_load_high"))
        recs.push("Reduce training ambition and bias toward recovery movement.");
    if (flags.has("low_activity") || flags.has("drift_risk"))
        recs.push("Take a 20-minute walk to create momentum.");
    if (flags.has("stress_high") || flags.has("mood_low"))
        recs.push("Pick the smallest useful plan and reduce optional commitments.");
    if (dayType === "gold")
        recs.push("Use the strong window for one important deep-work block.");
    recs.push("Start wind-down earlier tonight.");
    return [...new Set(recs)].slice(0, 5);
}
function nonNegotiable(dayType, signals) {
    const flags = new Set(signals.map((signal) => signal.flag));
    if (flags.has("protein_low"))
        return "Protein and water before noon.";
    if (flags.has("drift_risk"))
        return "One focused work block before distractions.";
    if (dayType === "red")
        return "Keep training and workload conservative today.";
    if (dayType === "blue")
        return "Use a simple schedule anchor before checking the day by mood.";
    return "Protect one meaningful work block.";
}
function caveats(snapshot) {
    const missing = snapshot.dataQuality?.missing ?? [];
    const warnings = snapshot.dataQuality?.warnings ?? [];
    return [
        ...warnings,
        ...(missing.length > 0 ? [`Missing provider fields: ${missing.join(", ")}.`] : []),
        "This brief is for planning and recovery habits, not healthcare guidance."
    ];
}
function similarSummary(days) {
    if (days.length === 0) {
        return "Not enough similar history yet. Log a few more mornings and check-ins to make this more useful.";
    }
    const repeated = days
        .flatMap((day) => day.overlappingFlags)
        .reduce((counts, flag) => {
        counts[flag] = (counts[flag] ?? 0) + 1;
        return counts;
    }, {});
    const topFlag = Object.entries(repeated).sort((a, b) => b[1] - a[1])[0]?.[0];
    return `Found ${days.length} similar past day${days.length === 1 ? "" : "s"}${topFlag ? `; recurring overlap is ${topFlag}` : ""}.`;
}
