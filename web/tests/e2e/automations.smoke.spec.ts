import { test, expect } from '@playwright/test';

test('create, run, and delete an automation', async ({ page, context }) => {
  const baseUrl = process.env.RUNTIME_BASE_URL!;
  const token = process.env.RUNTIME_TOKEN!;
  await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

  await page.goto(`${baseUrl}/automations`, { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('route-automations')).toBeVisible();

  await page.getByTestId('automation-new-link').click();
  await expect(page.getByTestId('route-automation-new')).toBeVisible();

  const name = `Smoke auto ${Date.now()}`;
  await page.getByTestId('automation-new-name').fill(name);
  await page.getByTestId('automation-new-description').fill('smoke test');
  await page.getByTestId('automation-new-steps').fill('do a thing\nthen another');
  await page.getByTestId('automation-new-submit').click();

  // Lands on detail page.
  await expect(page.getByTestId('route-automation-detail')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('automation-detail-steps').locator('li')).toHaveCount(2);

  // Run it; the meta line should update with a "last run" timestamp on reload.
  await page.getByTestId('automation-detail-run').click();
  await expect(page.getByTestId('automation-detail-meta')).toContainText('last run', { timeout: 5_000 });

  // Delete and confirm we land back at the list.
  await page.getByTestId('automation-detail-delete').click();
  await page.waitForURL((u) => u.pathname.endsWith('/automations'), { timeout: 5_000 });
  await expect(page.getByText(name)).toHaveCount(0);
});
