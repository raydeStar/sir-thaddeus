import { createConfiguredHealthRuntime } from "../runtimeFactory.js";
async function main() {
    const date = process.argv[2] ?? new Date().toISOString().slice(0, 10);
    const runtime = createConfiguredHealthRuntime({ today: () => date });
    await runtime.seed(date);
    console.log(`Seeded 30 days of health history before ${date}.`);
}
main().catch((error) => {
    console.error("Health Pack seed failed:", error instanceof Error ? error.message : String(error));
    process.exit(1);
});
