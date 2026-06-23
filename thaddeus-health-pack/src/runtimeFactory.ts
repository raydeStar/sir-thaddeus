import { HealthPackRuntime, HealthPackRuntimeOptions } from "./HealthPackRuntime.js";
import { createProviderConfigStore, createTokenStore, loadHealthPackConfig } from "./config.js";

export function createConfiguredHealthRuntime(options: HealthPackRuntimeOptions = {}): HealthPackRuntime {
  const config = loadHealthPackConfig();

  return new HealthPackRuntime({
    provider: options.provider,
    providerConfigStore: options.providerConfigStore ?? createProviderConfigStore(config),
    tokenStore: options.tokenStore ?? createTokenStore(config),
    storagePath: options.storagePath ?? config.storePath,
    auditPath: options.auditPath ?? config.auditPath,
    seedMockHistory: options.seedMockHistory,
    store: options.store,
    today: options.today
  });
}
