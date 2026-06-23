export class InMemoryHealthStore {
    dailyHealthSnapshots = new Map();
    dailyHealthBaselines = new Map();
    dailyStrategyBriefs = new Map();
    patternEpisodes = new Map();
    manualHealthCheckins = new Map();
    async saveDailySnapshot(snapshot) {
        this.dailyHealthSnapshots.set(snapshot.date, snapshot);
        return snapshot;
    }
    async getDailySnapshot(date) {
        return this.dailyHealthSnapshots.get(date);
    }
    async listDailySnapshots() {
        return [...this.dailyHealthSnapshots.values()].sort((a, b) => a.date.localeCompare(b.date));
    }
    async saveBaseline(baseline) {
        this.dailyHealthBaselines.set(baseline.date, baseline);
        return baseline;
    }
    async getBaseline(date) {
        return this.dailyHealthBaselines.get(date);
    }
    async saveStrategyBrief(brief) {
        this.dailyStrategyBriefs.set(brief.date, brief);
        return brief;
    }
    async getStrategyBrief(date) {
        return this.dailyStrategyBriefs.get(date);
    }
    async savePatternEpisode(episode) {
        this.patternEpisodes.set(episode.date, episode);
        return episode;
    }
    async listPatternEpisodes() {
        return [...this.patternEpisodes.values()].sort((a, b) => a.date.localeCompare(b.date));
    }
    async saveManualCheckin(checkin) {
        this.manualHealthCheckins.set(checkin.id, checkin);
        return checkin;
    }
    async listManualCheckins(date) {
        const checkins = [...this.manualHealthCheckins.values()];
        return (date ? checkins.filter((checkin) => checkin.date === date) : checkins)
            .sort((a, b) => a.createdAt.localeCompare(b.createdAt));
    }
}
