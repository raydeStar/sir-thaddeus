import { healthPackManifest } from "./manifest.js";
import { HealthPackRuntime } from "./HealthPackRuntime.js";
export function createHealthPackModule(options = {}) {
    const runtime = new HealthPackRuntime(withDefaultStorage(options));
    return {
        manifest: healthPackManifest,
        tools: runtime.tools(),
        jobs: runtime.jobs(),
        hooks: {
            on_module_installed: async () => {
                if (options.seedMockHistory ?? true) {
                    await runtime.seed();
                }
                return { seeded: options.seedMockHistory ?? true, provider: runtime.provider.providerName };
            },
            on_morning: async () => runtime.getMorningStrategyBrief()
        }
    };
}
function withDefaultStorage(options) {
    if (options.store || options.storagePath) {
        return options;
    }
    return {
        ...options,
        storagePath: "data/health-pack/health-store.json"
    };
}
