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

    const localOnly = page.getByTestId('settings-privacy-local-only');
    const wasChecked = await localOnly.isChecked();
    const localOnlySwitch = page.getByRole('switch', { name: 'Local-only mode' });
    await localOnlySwitch.scrollIntoViewIfNeeded();
    await localOnlySwitch.click();

    await page.getByTestId('settings-tab-models').click();
    const modelInput = page.getByTestId('settings-llm-model');
    await modelInput.fill('llama3.1:70b');

    // The gatekeeper status banner must render (regardless of reachability).
    // Its `data-state` is one of: active / unreachable / not-configured —
    // any value is fine here; we just want to confirm the banner is wired
    // to the status endpoint and shows up in the UI.
    const gkBanner = page.getByTestId('settings-gatekeeper-status');
    await expect(gkBanner).toBeVisible({ timeout: 5_000 });
    const gkState = await gkBanner.getAttribute('data-state');
    expect(['active', 'unreachable', 'not-configured']).toContain(gkState);

    // Save.
    await page.getByTestId('settings-save').click();
    await expect(page.getByTestId('settings-saved')).toBeVisible({ timeout: 5_000 });

    // Reload and verify.
    await page.reload();
    await expect(page.getByTestId('settings-form')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('settings-shortcut-ptt')).toHaveValue('Ctrl+Alt+M');
    if (wasChecked) await expect(page.getByTestId('settings-privacy-local-only')).not.toBeChecked();
    else await expect(page.getByTestId('settings-privacy-local-only')).toBeChecked();
    await page.getByTestId('settings-tab-models').click();
    await expect(page.getByTestId('settings-llm-model')).toHaveValue('llama3.1:70b');
  });
});
