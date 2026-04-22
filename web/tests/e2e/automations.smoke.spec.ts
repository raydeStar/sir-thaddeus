import { test, expect } from '@playwright/test';

// Cold-start on a large local model can push past the global 30s budget
// when both automation steps need to finish streaming.
test.setTimeout(90_000);

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

  // Run it; the app now navigates into the execution thread so the user can
  // watch the run live, then the list reflects the recorded last-run state.
  await page.getByTestId('automation-detail-run').click();
  await expect(page.getByTestId('route-chat-thread')).toBeVisible({ timeout: 10_000 });

  // Both step texts should eventually render as user bubbles in the run
  // thread. Previously only step 1 was shown because the server appended
  // subsequent user messages without a WS broadcast, so the chat store
  // never knew they existed.
  const threadList = page.getByTestId('chat-message-list');
  await expect(threadList).toContainText('do a thing', { timeout: 10_000 });
  await expect(threadList).toContainText('then another', { timeout: 30_000 });

  await page.goto(`${baseUrl}/automations`, { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('route-automations')).toBeVisible();
  const item = page
    .getByTestId('automation-list')
    .locator('li')
    .filter({ hasText: name })
    .first();
  await expect(item).toContainText('last run', { timeout: 10_000 });

  // Delete and confirm we land back at the list.
  await item.getByRole('link', { name }).click();
  await expect(page.getByTestId('route-automation-detail')).toBeVisible({ timeout: 10_000 });
  await page.getByTestId('automation-detail-delete').click();
  await page.waitForURL((u) => u.pathname.endsWith('/automations'), { timeout: 10_000 });
  await expect(page.getByText(name)).toHaveCount(0);
});
