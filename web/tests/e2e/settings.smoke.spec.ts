import { test, expect } from '@playwright/test';

/**
 * Phase 6.3 smoke. Verifies the Settings surface end-to-end: load the
 * settings page, change a couple of values across categories, save, and
 * re-load to confirm the values were persisted.
 */
test.describe('settings smoke', () => {
  test('user can edit and persist settings', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl).toBeTruthy();
    expect(token).toBeTruthy();
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    await page.goto(`${baseUrl}/settings`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('route-settings')).toBeVisible();
    await expect(page.getByTestId('settings-form')).toBeVisible({ timeout: 10_000 });

    // Change a few fields across sections.
    const pttInput = page.getByTestId('settings-shortcut-ptt');
    await pttInput.fill('Ctrl+Alt+M');

    // Privacy toggles now live under the Permissions tab (right after General).
    await page.getByTestId('settings-tab-permissions').click();
    const localOnly = page.getByTestId('settings-privacy-local-only');
    const wasChecked = await localOnly.isChecked();
    const localOnlySwitch = page.getByRole('switch', { name: 'Local-only mode' });
    await localOnlySwitch.scrollIntoViewIfNeeded();
    await localOnlySwitch.click();

    await page.getByTestId('settings-tab-models').click();
    const modelInput = page.getByTestId('settings-llm-model');
    await modelInput.fill('llama3.1:70b');

    // The gatekeeper status banner must render (regardless of reachability).
    // Its `data-state` is one of: checking / active / unreachable /
    // not-configured. Any value is fine here; we just want to confirm the
    // banner is wired to the status endpoint and shows up in the UI.
    const gkBanner = page.getByTestId('settings-gatekeeper-status');
    await expect(gkBanner).toBeVisible({ timeout: 5_000 });
    const gkState = await gkBanner.getAttribute('data-state');
    expect(['checking', 'active', 'unreachable', 'not-configured']).toContain(gkState);

    // Diagnostics is a separately owned settings feature. Keep its tab and
    // both viewer modes covered when the route is reorganized.
    await page.getByTestId('settings-tab-logs').click();
    await expect(page.getByTestId('settings-logs-paths')).toBeVisible();
    await expect(page.getByTestId('settings-logs-pane-traces')).toBeVisible();
    await page.getByTestId('settings-logs-pane-runtime').click();
    await expect(page.getByTestId('settings-logs-pane-runtime')).toHaveAttribute('aria-selected', 'true');
    await page.getByTestId('settings-logs-pane-audit').click();
    await expect(page.getByTestId('settings-audit-insights')).toBeVisible();
    await expect(page.getByTestId('insight-task-completion')).toBeVisible();
    await expect(page.getByTestId('insight-trust-calibration')).toContainText(/Needs evidence|\d+%/);
    await page.getByTestId('audit-filter').fill('permission');
    await expect(page.getByTestId('audit-export')).toBeVisible();

    const insightsResponse = await context.request.get(`${baseUrl}/api/insights`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(insightsResponse.ok()).toBeTruthy();
    const auditExport = await context.request.get(`${baseUrl}/api/audit/export`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(auditExport.ok()).toBeTruthy();
    expect(auditExport.headers()['content-type']).toContain('application/x-ndjson');

    // Save.
    await page.getByTestId('settings-save').click();
    await expect(page.getByTestId('settings-saved')).toBeVisible({ timeout: 5_000 });

    // Reload and verify.
    await page.reload();
    await expect(page.getByTestId('settings-form')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('settings-shortcut-ptt')).toHaveValue('Ctrl+Alt+M');
    await page.getByTestId('settings-tab-permissions').click();
    if (wasChecked) await expect(page.getByTestId('settings-privacy-local-only')).not.toBeChecked();
    else await expect(page.getByTestId('settings-privacy-local-only')).toBeChecked();
    await page.getByTestId('settings-tab-models').click();
    await expect(page.getByTestId('settings-llm-model')).toHaveValue('llama3.1:70b');
  });
});
