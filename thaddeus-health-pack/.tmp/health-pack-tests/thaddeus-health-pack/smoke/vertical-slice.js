import { AgentRuntime } from "../../sir-thaddeus-core/src/assistant/AgentRuntime.js";
import { createHealthPackModule } from "../src/module.js";
async function main() {
    const date = "2026-06-02";
    const runtime = new AgentRuntime();
    await runtime.installModule(createHealthPackModule({ today: () => date }));
    await runtime.approveModule("com.thaddeus.health");
    const snapshot = await runtime.invokeTool({ name: "health.get_daily_snapshot", args: { date } });
    const baseline = await runtime.invokeTool({ name: "health.get_baselines", args: { date } });
    const brief = await runtime.invokeTool({
        name: "health.get_morning_strategy_brief",
        args: { date }
    });
    const checkin = await runtime.invokeTool({
        name: "health.log_manual_checkin",
        args: {
            date,
            subjective: { energy: 4, stress: 8, soreness: 6, mood: "flat", focus: 4 },
            nutrition: { proteinEstimate: 48, hydrationEstimate: 50 },
            notes: "Smoke-test check-in."
        }
    });
    const similarDays = await runtime.invokeTool({ name: "health.get_similar_past_days", args: { date } });
    if (!brief.recommendations.length || !brief.nonNegotiable) {
        throw new Error("Morning strategy brief did not include recommendations and a non-negotiable.");
    }
    console.log(JSON.stringify({
        moduleCount: runtime.snapshot().modules.length,
        snapshotDate: snapshot.date,
        baselineDate: baseline.date,
        dayType: brief.dayType,
        recommendationCount: brief.recommendations.length,
        checkinDate: checkin.date,
        similarDayCount: similarDays.length
    }, null, 2));
}
main().catch((error) => {
    console.error(error);
    throw error;
});
