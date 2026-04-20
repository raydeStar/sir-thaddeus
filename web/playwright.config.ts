import { defineConfig } from '@playwright/test';

/**
 * Playwright config for the Phase-1 smoke suite. The runtime is spawned by
 * `tests/e2e/global-setup.ts`, which writes its base URL into
 * `process.env.RUNTIME_BASE_URL` and bearer token into `process.env.RUNTIME_TOKEN`.
 * Tests pull both via the helper in `tests/e2e/runtime-context.ts`.
 */
export default defineConfig({
  testDir: './tests/e2e',
  timeout: 30_000,
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  globalSetup: './tests/e2e/global-setup.ts',
  globalTeardown: './tests/e2e/global-teardown.ts',
  use: {
    actionTimeout: 5_000,
    navigationTimeout: 10_000,
    headless: true,
  },
  projects: [
    {
      name: 'chromium',
      use: {
        browserName: 'chromium',
      },
    },
  ],
});
