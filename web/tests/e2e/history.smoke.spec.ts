import { test, expect } from '@playwright/test';

/**
 * Phase 4 smoke. Exercises the History route: create two threads, then on the
 * History page rename one, pin the other, search by title fragment, and delete
 * the renamed thread. Verifies that pin moves a thread into the Pinned section.
 */
test.describe('history smoke', () => {
  test('user can rename, pin, search, and delete threads from history', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl).toBeTruthy();
    expect(token).toBeTruthy();

    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    // Create two threads via the chat surface so the IDs land in the runtime store.
    await page.goto(`${baseUrl}/chat`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('route-chat')).toBeVisible();

    await page.getByTestId('chat-new-thread').click();
    await expect(page.getByTestId('route-chat-thread')).toBeVisible({ timeout: 10_000 });
    const firstUrl = page.url();
    const firstId = firstUrl.split('/chat/').pop()!;

    await page.goto(`${baseUrl}/chat`, { waitUntil: 'domcontentloaded' });
    await page.getByTestId('chat-new-thread').click();
    await expect(page.getByTestId('route-chat-thread')).toBeVisible({ timeout: 10_000 });
    const secondUrl = page.url();
    const secondId = secondUrl.split('/chat/').pop()!;

    expect(firstId).not.toEqual(secondId);

    // Navigate to History.
    await page.goto(`${baseUrl}/history`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('route-history')).toBeVisible();

    const firstRow = page.getByTestId(`history-thread-${firstId}`);
    const secondRow = page.getByTestId(`history-thread-${secondId}`);
    await expect(firstRow).toBeVisible({ timeout: 10_000 });
    await expect(secondRow).toBeVisible();

    // Rename the first thread via window.prompt — stub it before clicking.
    await page.evaluate(() => {
      (window as unknown as { prompt: (m?: string, d?: string) => string | null }).prompt = () =>
        'Renamed Smoke Thread';
    });
    await page.getByTestId(`history-rename-${firstId}`).click();
    await expect(page.getByTestId(`history-thread-${firstId}`)).toContainText(
      'Renamed Smoke Thread',
      { timeout: 5_000 },
    );

    // Pin the second thread; it should move into the Pinned section.
    await page.getByTestId(`history-pin-${secondId}`).click();
    const pinnedSection = page.getByTestId('history-pinned');
    await expect(pinnedSection).toBeVisible({ timeout: 5_000 });
    await expect(pinnedSection.getByTestId(`history-thread-${secondId}`)).toBeVisible();

    // Search by the renamed title fragment — only the renamed thread should remain.
    await page.getByTestId('history-search').fill('Renamed Smoke');
    await expect(page.getByTestId(`history-thread-${firstId}`)).toBeVisible();
    await expect(page.getByTestId(`history-thread-${secondId}`)).toHaveCount(0);

    // Clear search.
    await page.getByTestId('history-search').fill('');

    // Delete the renamed thread via window.confirm — stub it true.
    await page.evaluate(() => {
      (window as unknown as { confirm: (m?: string) => boolean }).confirm = () => true;
    });
    await page.getByTestId(`history-delete-${firstId}`).click();
    await expect(page.getByTestId(`history-thread-${firstId}`)).toHaveCount(0, { timeout: 5_000 });

    // The pinned thread should still be there.
    await expect(page.getByTestId(`history-thread-${secondId}`)).toBeVisible();
  });
});
