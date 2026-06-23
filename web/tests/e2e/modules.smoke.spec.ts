import { test, expect } from '@playwright/test';

test.describe('modules smoke', () => {
  test('user can use Health Pack from the Data page', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl).toBeTruthy();
    expect(token).toBeTruthy();

    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    let approvalStatus: 'Pending' | 'Approved' | 'Denied' = 'Approved';
    const auditEvents = [
      {
        id: 'ma_fixture_1',
        moduleId: 'com.thaddeus.health',
        action: 'module.discovered',
        result: 'ok',
        at: '2026-06-03T12:00:00Z',
        message: null,
        toolName: null,
      },
    ];

    const moduleSummary = () => ({
      id: 'com.thaddeus.health',
      name: 'Health Pack',
      version: '0.1.0',
      description: 'Adds health snapshots, baselines, and morning strategy briefs.',
      manifestPath: 'C:\\Users\\Ayric\\Source\\Repos\\sir-thaddeus\\thaddeus-health-pack\\manifest.json',
      status: approvalStatus === 'Approved' ? 'approved' : 'pending',
      approvalStatus,
      disabled: false,
      permissionCount: 9,
      toolCount: 17,
      lastStatusCheck: null,
      lastInvocation: null,
      lastError: null,
    });

    const moduleDetail = () => ({
      ...moduleSummary(),
      requestedPermissions: {
        externalAccounts: [{ provider: 'google-health', scopes: ['sleep.read', 'heart_rate.read'] }],
        memory: { read: ['fitness_goals'], write: ['daily_health_snapshots'] },
        notifications: ['morning_strategy_brief'],
      },
      tools: [
        { name: 'health.provider_status', description: null, inputSchema: null, canInvokeManually: true },
        { name: 'health.secret_store_status', description: null, inputSchema: null, canInvokeManually: true },
        { name: 'health.backfill', description: null, inputSchema: null, canInvokeManually: true },
        { name: 'health.get_morning_strategy_brief', description: null, inputSchema: null, canInvokeManually: true },
      ],
      jobs: ['health.morning_strategy_job'],
      hooks: ['on_module_installed', 'on_morning'],
      memoryNamespaces: ['daily_health_snapshots'],
      execution: {
        type: 'stdio',
        command: 'npm',
        args: ['run', 'mcp'],
        cwd: 'C:\\Users\\Ayric\\Source\\Repos\\sir-thaddeus\\thaddeus-health-pack',
        envKeys: ['HEALTH_DATA_PROVIDER', 'GOOGLE_HEALTH_CLIENT_SECRET'],
      },
      recentAuditEvents: auditEvents,
    });

    await page.route(`${baseUrl}/api/modules`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ modules: [moduleSummary()] }),
      });
    });

    await page.route(`${baseUrl}/api/modules/com.thaddeus.health`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify(moduleDetail()),
      });
    });

    await page.route(`${baseUrl}/api/modules/com.thaddeus.health/approve`, async (route) => {
      approvalStatus = 'Approved';
      auditEvents.unshift({
        id: 'ma_fixture_2',
        moduleId: 'com.thaddeus.health',
        action: 'module.approved',
        result: 'ok',
        at: '2026-06-03T12:01:00Z',
        message: null,
        toolName: null,
      });
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify(moduleDetail()),
      });
    });

    await page.route(`${baseUrl}/api/modules/com.thaddeus.health/tools/**/invoke`, async (route) => {
      const toolName = decodeURIComponent(route.request().url().split('/tools/')[1].split('/invoke')[0]);
      auditEvents.unshift({
        id: 'ma_fixture_3',
        moduleId: 'com.thaddeus.health',
        action: 'module.tool_invoked',
        result: 'ok',
        at: '2026-06-03T12:02:00Z',
        message: null,
        toolName,
      });
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          moduleId: 'com.thaddeus.health',
          toolName,
          ok: true,
          content: JSON.stringify({
            providerName: 'google-health',
            lifecycle: 'auth_required',
            connected: false,
            credentials: { clientId: true, clientSecret: true },
            sync: { snapshotCount: 0, warnings: ['OAuth is not complete.'] },
          }),
          json: {
            providerName: 'google-health',
            lifecycle: 'auth_required',
            connected: false,
            credentials: { clientId: true, clientSecret: true },
            sync: { snapshotCount: 0, warnings: ['OAuth is not complete.'] },
          },
          invokedAt: '2026-06-03T12:02:00Z',
        }),
      });
    });

    await page.goto(`${baseUrl}/modules`, { waitUntil: 'domcontentloaded' });

    await expect(page.getByTestId('route-data')).toBeVisible();
    await expect(page.getByTestId('data-health-panel')).toBeVisible();
    await expect(page.getByTestId('data-sources-panel')).toContainText('Health Pack');

    await page.getByRole('button', { name: 'Provider Status' }).click();
    await expect(page.getByText('Status')).toBeVisible();
    await expect(page.getByText('google-health')).toBeVisible();
    await expect(page.getByText('auth_required', { exact: true })).toBeVisible();
  });
});
