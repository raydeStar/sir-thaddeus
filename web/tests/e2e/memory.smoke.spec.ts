import { test, expect } from '@playwright/test';

test('memo create, pin, delete', async ({ page, context }) => {
  const baseUrl = process.env.RUNTIME_BASE_URL!;
  const token = process.env.RUNTIME_TOKEN!;
  await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

  await page.goto(`${baseUrl}/memory`, { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('route-memory')).toBeVisible();

  const title = `Smoke memo ${Date.now()}`;
  await page.getByTestId('memo-create-title').fill(title);
  await page.getByTestId('memo-create-body').fill('hello world');
  await page.getByTestId('memo-create-tags').fill('test, smoke');
  await page.getByTestId('memo-create-submit').click();

  const item = page.locator('[data-testid^="memo-item-"]').filter({ hasText: title });
  await expect(item).toBeVisible({ timeout: 5_000 });

  // Pin then verify still present (pinned-first ordering keeps it visible).
  await item.locator('[data-testid^="memo-pin-"]').click();
  await expect(item).toBeVisible();

  // Delete; the item should disappear.
  await item.locator('[data-testid^="memo-delete-"]').click();
  await expect(item).toHaveCount(0, { timeout: 5_000 });
});
