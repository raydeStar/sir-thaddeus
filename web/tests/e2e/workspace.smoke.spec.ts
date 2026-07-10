import { test, expect } from '@playwright/test';

/**
 * Phase-1 smoke test. Verifies the runtime serves the workspace SPA, the meta-tag
 * bootstrap is present with a real token, the WebSocket connects, and the state
 * badge displays the runtime's authoritative state (Idle on a cold start).
 */
test.describe('workspace smoke', () => {
  test('renders the workspace and connects to the runtime', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl, 'global-setup must populate RUNTIME_BASE_URL').toBeTruthy();
    expect(token, 'global-setup must populate RUNTIME_TOKEN').toBeTruthy();

    // The runtime guards every request with bearer auth. Inject the token into the
    // Authorization header for every request the browser makes (including the
    // initial document fetch and the chunked JS).
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    await page.goto(`${baseUrl}/`, { waitUntil: 'domcontentloaded' });

    // Workspace root mounted.
    await expect(page.getByTestId('workspace-root')).toBeVisible();
    await expect(page.getByTestId('route-home').getByTestId('thaddeus-signet')).toBeVisible();

    // Meta-tag bootstrap is present and the version meta matches the runtime's lock.
    const tokenMeta = await page.locator('meta[name="thaddeus-runtime-token"]').getAttribute('content');
    expect(tokenMeta).toBe(token);

    // The state badge eventually reflects the cold-start Idle state delivered over
    // the WebSocket. The badge writes data-state on the wrapping element.
    await expect(page.getByTestId('runtime-state-badge')).toHaveAttribute('data-state', 'Idle', { timeout: 10_000 });

    // Connection dot becomes "connected" once the WS handshake completes.
    await expect(page.getByTestId('runtime-connection-dot')).toHaveAttribute('data-connected', 'true', { timeout: 10_000 });
  });
});
