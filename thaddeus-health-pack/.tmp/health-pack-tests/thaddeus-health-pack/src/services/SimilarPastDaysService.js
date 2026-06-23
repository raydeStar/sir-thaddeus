export class SimilarPastDaysService {
    store;
    constructor(store) {
        this.store = store;
    }
    async find(date, flags, limit = 5) {
        if (flags.length === 0) {
            return [];
        }
        const requested = new Set(flags);
        const episodes = (await this.store.listPatternEpisodes())
            .filter((episode) => episode.date < date)
            .map((episode) => ({
            episode,
            overlappingFlags: episode.flags.filter((flag) => requested.has(flag))
        }))
            .filter((item) => item.overlappingFlags.length > 0)
            .sort((a, b) => {
            const overlap = b.overlappingFlags.length - a.overlappingFlags.length;
            return overlap !== 0 ? overlap : b.episode.date.localeCompare(a.episode.date);
        })
            .slice(0, limit);
        return episodes.map(({ episode, overlappingFlags }) => ({
            date: episode.date,
            dayType: episode.dayType,
            overlappingFlags,
            summary: summarizeEpisode(episode, overlappingFlags)
        }));
    }
}
function summarizeEpisode(episode, overlappingFlags) {
    const outcome = episode.after
        ? `Afterward: energy ${episode.after.energy ?? "unknown"}, focus ${episode.after.focus ?? "unknown"}, mood ${episode.after.mood ?? "unknown"}.`
        : "No follow-up outcome was logged.";
    const helped = episode.recommendationsHelped === undefined
        ? ""
        : episode.recommendationsHelped
            ? " Recommendations appeared to help."
            : " Recommendations did not clearly help.";
    return `${episode.summary} Overlap: ${overlappingFlags.join(", ")}. ${outcome}${helped} ${episode.repeatedImprovement ?? ""}`.trim();
}
