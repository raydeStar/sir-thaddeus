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

  test('the runtime socket redials after the connection drops', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL!;
    const token = process.env.RUNTIME_TOKEN!;
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    // Proxy /ws so the test can sever the connection the way a runtime restart
    // or a machine wake would. Without a redial the whole app goes quiet —
    // streaming text, tool pills, permission prompts, run state — with no
    // indication beyond the connection dot, and only a reload recovers it.
    let connectionCount = 0;
    const routes: Array<{ close: () => void }> = [];
    await page.routeWebSocket(/\/ws/, (ws) => {
      connectionCount += 1;
      routes.push(ws);
      // No message handlers: Playwright relays both directions automatically.
      ws.connectToServer();
    });

    await page.goto(`${baseUrl}/`, { waitUntil: 'domcontentloaded' });
    const dot = page.getByTestId('runtime-connection-dot');
    await expect(dot).toHaveAttribute('data-connected', 'true', { timeout: 10_000 });
    expect(connectionCount).toBe(1);

    routes[0].close();

    // A fresh connection is established without a reload, and the app reports
    // itself connected again.
    await expect
      .poll(() => connectionCount, { timeout: 15_000 })
      .toBeGreaterThan(1);
    await expect(dot).toHaveAttribute('data-connected', 'true', { timeout: 15_000 });
    await expect(page.getByTestId('runtime-state-badge')).toHaveAttribute('data-state', 'Idle', {
      timeout: 15_000,
    });
  });

  test('leaving compact mode does not disconnect the application socket', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL!;
    const token = process.env.RUNTIME_TOKEN!;
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    await page.goto(`${baseUrl}/`, { waitUntil: 'domcontentloaded' });
    const dot = page.getByTestId('runtime-connection-dot');
    await expect(dot).toHaveAttribute('data-connected', 'true', { timeout: 10_000 });

    // Navigate client-side so the root layout stays mounted while CompactRoute
    // mounts and unmounts. The previous compact cleanup closed the singleton
    // socket even though the root layout still owned the live application.
    await page.evaluate(() => {
      window.history.pushState({}, '', '/compact');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    await expect(page.getByTestId('route-compact')).toBeVisible();

    await page.evaluate(() => window.history.back());
    await expect(page.getByTestId('route-home')).toBeVisible({ timeout: 10_000 });
    await expect(dot).toHaveAttribute('data-connected', 'true', { timeout: 10_000 });
  });
});
