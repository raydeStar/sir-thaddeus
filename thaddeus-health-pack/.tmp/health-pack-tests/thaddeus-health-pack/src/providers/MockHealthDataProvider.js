export class MockHealthDataProvider {
    providerName = "mock";
    async getDailySnapshot(date) {
        const seed = hashDate(date);
        const sleepMinutes = 340 + (seed % 210);
        const hrv = 34 + (seed % 38);
        const restingHeartRate = 55 + (seed % 17);
        const steps = 2600 + ((seed * 73) % 10400);
        const hardWorkout = seed % 9 === 0;
        const stress = 2 + (seed % 8);
        const soreness = hardWorkout ? 8 : 2 + (seed % 5);
        return {
            date,
            provider: this.providerName,
            sleep: {
                durationMinutes: sleepMinutes,
                score: clamp(Math.round((sleepMinutes / 510) * 100), 45, 96),
                startTime: `${date}T22:45:00`,
                endTime: `${date}T06:45:00`,
                quality: "mock"
            },
            recovery: {
                restingHeartRate,
                hrv,
                respiratoryRate: 14 + (seed % 4),
                spo2: 96 + (seed % 3),
                quality: "mock"
            },
            heart: {
                restingHeartRate,
                hrv,
                respiratoryRate: 14 + (seed % 4),
                spo2: 96 + (seed % 3),
                quality: "mock"
            },
            activity: {
                steps,
                activeMinutes: Math.round(steps / 170),
                workoutMinutes: hardWorkout ? 62 : seed % 4 === 0 ? 28 : 0,
                workoutIntensity: hardWorkout ? "hard" : seed % 4 === 0 ? "moderate" : "none",
                workouts: [
                    {
                        durationMinutes: hardWorkout ? 62 : seed % 4 === 0 ? 28 : 0,
                        intensity: hardWorkout ? "hard" : seed % 4 === 0 ? "moderate" : "none",
                        label: hardWorkout ? "Mock hard training" : "Mock daily movement"
                    }
                ],
                priorDayLoad: hardWorkout || steps > 10500 ? "high" : steps < 4000 ? "low" : "normal",
                quality: "mock"
            },
            nutrition: {
                caloriesEstimate: 1800 + (seed % 900),
                proteinEstimate: 55 + (seed % 95),
                hydrationEstimate: 55 + (seed % 45),
                caffeineAfterNoon: seed % 6 === 0
            },
            subjective: {
                mood: seed % 11 === 0 ? "low" : seed % 5 === 0 ? "flat" : "steady",
                energy: clamp(3 + (seed % 7), 1, 10),
                stress,
                soreness,
                focus: clamp(4 + ((seed * 3) % 6), 1, 10)
            },
            dataQuality: {
                provider: this.providerName,
                quality: "mock",
                generatedAt: new Date().toISOString(),
                missing: [],
                warnings: ["Mock data for local development and tests."]
            }
        };
    }
    async getStatus() {
        return {
            providerName: this.providerName,
            selectedProvider: this.providerName,
            lifecycle: "connected",
            configured: true,
            authenticated: true,
            connected: true,
            mode: "mock",
            missingConfig: [],
            credentials: {},
            scopes: [],
            warnings: ["Using deterministic mock health data."],
            errors: []
        };
    }
}
export class MockHealthProvider extends MockHealthDataProvider {
}
export async function buildSeededMockHistory(provider, throughDate, days = 30) {
    const snapshots = [];
    const end = parseIsoDate(throughDate);
    for (let offset = days; offset >= 1; offset -= 1) {
        const date = formatIsoDate(addDays(end, -offset));
        snapshots.push(await provider.getDailySnapshot(date));
    }
    return snapshots;
}
function hashDate(date) {
    return [...date].reduce((total, char) => total + char.charCodeAt(0) * 17, 0);
}
function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
}
function parseIsoDate(date) {
    return new Date(`${date}T00:00:00Z`);
}
function addDays(date, days) {
    const next = new Date(date);
    next.setUTCDate(next.getUTCDate() + days);
    return next;
}
function formatIsoDate(date) {
    return date.toISOString().slice(0, 10);
}
