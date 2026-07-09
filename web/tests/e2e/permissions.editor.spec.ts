import { test, expect } from '@playwright/test';

/**
 * Cascading per-tool permission editor + modal scope checkbox.
 *
 * The catalog / respond endpoints are mocked with page.route (same pattern
 * as modules.smoke.spec.ts) so the assertions pin the UI to the wire
 * contract rather than to whatever the local runtime build supports.
 */

const CATALOG_FIXTURE = {
  developerOverride: 'none',
  groups: [
    { key: 'screen', policy: 'ask', tools: [{ name: 'screen_capture', override: null, effective: 'ask' }] },
    { key: 'files', policy: 'ask', tools: [{ name: 'file_read', override: null, effective: 'ask' }] },
    { key: 'system', policy: 'ask', tools: [{ name: 'shell_exec', override: null, effective: 'ask' }] },
    {
      key: 'web',
      policy: 'ask',
      tools: [
        { name: 'web_search', override: null, effective: 'ask' },
        { name: 'weather_geocode', override: null, effective: 'ask' },
      ],
    },
    { key: 'memoryRead', policy: 'always', tools: [{ name: 'memory_lookup', override: null, effective: 'always' }] },
    { key: 'memoryWrite', policy: 'ask', tools: [{ name: 'memory_save', override: null, effective: 'ask' }] },
  ],
};

test.describe('permissions editor', () => {
  test('user can cascade group policies into per-tool overrides and save them', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl).toBeTruthy();
    expect(token).toBeTruthy();
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    await page.route(`${baseUrl}/api/permissions/catalog`, async (route) => {
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify(CATALOG_FIXTURE) });
    });

    // Capture saves; echo the submitted document back like the real endpoint.
    const savedDocs: Array<Record<string, unknown>> = [];
    await page.route(`${baseUrl}/api/settings`, async (route) => {
      if (route.request().method() !== 'PUT') {
        await route.fallback();
        return;
      }
      const body = route.request().postDataJSON() as Record<string, unknown>;
      savedDocs.push(body);
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
    });

    await page.goto(`${baseUrl}/settings`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('settings-form')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('settings-tab-permissions').click();
    await expect(page.getByTestId('settings-permissions-panel')).toBeVisible();
    await expect(page.getByTestId('settings-permissions-developer-override')).toBeVisible();

    // Catalog-fed counts for the web group.
    await expect(page.getByTestId('settings-permissions-counts-web')).toHaveText('2 tools · 0 overrides');

    // A direct dangerous-group edit must disable the global developer
    // override; otherwise the visible group selection saves but has no effect.
    await page.getByTestId('settings-permissions-developer-override').selectOption('always');
    await page.getByTestId('settings-permissions-policy-web').selectOption('ask');
    await expect(page.getByTestId('settings-permissions-developer-override')).toHaveValue('none');

    // Pin the group policy, then check the cascade: inherit rows follow it live.
    await page.getByTestId('settings-permissions-expand-web').click();
    const searchRow = page.getByTestId('settings-permissions-tool-web-web_search');
    await expect(searchRow).toContainText('inherits Ask');
    await page.getByTestId('settings-permissions-policy-web').selectOption('always');
    await expect(searchRow).toContainText('inherits Always');

    // Per-tool override → badge + counts update.
    await page.getByTestId('settings-permissions-tool-policy-web_search').selectOption('off');
    await expect(page.getByTestId('settings-permissions-override-badge-web_search')).toBeVisible();
    await expect(page.getByTestId('settings-permissions-counts-web')).toHaveText('2 tools · 1 override');

    // Reset the group's overrides — the escape hatch.
    await page.getByTestId('settings-permissions-reset-web').click();
    await expect(page.getByTestId('settings-permissions-override-badge-web_search')).not.toBeVisible();
    await expect(page.getByTestId('settings-permissions-counts-web')).toHaveText('2 tools · 0 overrides');

    // Set an override again and save: the PUT document must carry it.
    await page.getByTestId('settings-permissions-tool-policy-web_search').selectOption('off');
    await page.getByTestId('settings-save').click();
    await expect(page.getByTestId('settings-saved')).toBeVisible({ timeout: 5_000 });
    expect(savedDocs.length).toBe(1);
    const firstSave = savedDocs[0].permissions as {
      developerOverride: string;
      web: string;
      toolOverrides?: Record<string, string>;
    };
    expect(firstSave.developerOverride).toBe('none');
    expect(firstSave.web).toBe('always');
    expect(firstSave.toolOverrides).toEqual({ web_search: 'off' });

    // Back to Inherit: the key is dropped and an empty map is omitted entirely.
    await page.getByTestId('settings-permissions-tool-policy-web_search').selectOption('inherit');
    await page.getByTestId('settings-save').click();
    await expect.poll(() => savedDocs.length, { timeout: 5_000 }).toBe(2);
    const secondSave = savedDocs[1].permissions as { toolOverrides?: Record<string, string> };
    expect(secondSave.toolOverrides).toBeUndefined();
  });

  test('permission modal scope checkbox scopes session/always decisions', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl).toBeTruthy();
    expect(token).toBeTruthy();
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    const pending = [
      {
        id: 'perm_fixture_1',
        tool: 'web_search',
        group: 'Web',
        argsJson: '{"query":"weather"}',
        threadId: 't1',
        turnId: 'u1',
        createdAt: '2026-07-09T12:00:00Z',
        // scope intentionally omitted → defaults to 'group'
      },
      {
        id: 'perm_fixture_2',
        tool: 'weather_geocode',
        group: 'Web',
        argsJson: '{"place":"Salt Lake City"}',
        threadId: 't1',
        turnId: 'u1',
        createdAt: '2026-07-09T12:00:01Z',
        scope: 'tool',
      },
    ];

    await page.route(`${baseUrl}/api/permissions/pending`, async (route) => {
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ requests: pending }) });
    });

    const responses: Array<Record<string, unknown>> = [];
    await page.route(`${baseUrl}/api/permissions/respond`, async (route) => {
      responses.push(route.request().postDataJSON() as Record<string, unknown>);
      await route.fulfill({ contentType: 'application/json', body: '{}' });
    });

    await page.goto(`${baseUrl}/settings`, { waitUntil: 'domcontentloaded' });

    const modal = page.getByTestId('permission-modal');
    await expect(modal).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('permission-modal-tool')).toContainText('web_search');
    await expect(modal).toContainText('Apply to all Web tools');
    await expect(modal).toContainText('Settings → Permissions');

    // Request without scope → defaults to group-wide (checked).
    const checkbox = page.getByTestId('permission-scope-checkbox');
    await expect(checkbox).toBeChecked();
    await page.getByTestId('permission-session').click();
    await expect
      .poll(() => responses.length, { timeout: 5_000 })
      .toBe(1);
    expect(responses[0]).toEqual({ id: 'perm_fixture_1', decision: 'session', scope: 'group' });

    // Next prompt arrives with scope 'tool' → checkbox resets to unchecked.
    await expect(page.getByTestId('permission-modal-tool')).toContainText('weather_geocode');
    await expect(checkbox).not.toBeChecked();
    await page.getByTestId('permission-always').click();
    await expect
      .poll(() => responses.length, { timeout: 5_000 })
      .toBe(2);
    expect(responses[1]).toEqual({ id: 'perm_fixture_2', decision: 'always', scope: 'tool' });
  });
});
