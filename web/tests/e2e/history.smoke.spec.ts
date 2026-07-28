import { test, expect } from '@playwright/test';

/**
 * Wedge 1 smoke. Exercises the canonical Conversations route: create two
 * threads, follow the legacy History redirect, rename one, pin the other,
 * search by title fragment, and delete the renamed thread.
 */
test.describe('conversation library smoke', () => {
  test('user can rename, pin, search, and delete conversations', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl).toBeTruthy();
    expect(token).toBeTruthy();

    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    await page.goto(`${baseUrl}/chat`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('route-chat')).toBeVisible();

    await page.getByTestId('chat-new-thread').click();
    await expect(page.getByTestId('route-chat-thread')).toBeVisible({ timeout: 10_000 });
    const firstId = page.url().split('/chat/').pop()!;

    await page.goto(`${baseUrl}/chat`, { waitUntil: 'domcontentloaded' });
    await page.getByTestId('chat-new-thread').click();
    await expect(page.getByTestId('route-chat-thread')).toBeVisible({ timeout: 10_000 });
    const secondId = page.url().split('/chat/').pop()!;

    expect(firstId).not.toEqual(secondId);

    // Old bookmarks now replace themselves with the canonical list route.
    await page.goto(`${baseUrl}/history`, { waitUntil: 'domcontentloaded' });
    await expect(page).toHaveURL(/\/chat$/);
    await expect(page.getByTestId('route-chat')).toBeVisible();

    const firstRow = page.getByTestId(`conversation-thread-${firstId}`);
    const secondRow = page.getByTestId(`conversation-thread-${secondId}`);
    await expect(firstRow).toBeVisible({ timeout: 10_000 });
    await expect(secondRow).toBeVisible();

    await page.evaluate(() => {
      (window as unknown as { prompt: () => string | null }).prompt = () =>
        'Renamed Smoke Thread';
    });
    await firstRow.getByRole('button', { name: /Rename / }).click();
    await expect(page.getByTestId(`conversation-thread-${firstId}`)).toContainText(
      'Renamed Smoke Thread',
      { timeout: 5_000 },
    );

    await secondRow.getByRole('button', { name: /Pin / }).click();
    const pinnedSection = page.getByTestId('conversations-pinned');
    await expect(pinnedSection).toBeVisible({ timeout: 5_000 });
    await expect(pinnedSection.getByTestId(`conversation-thread-${secondId}`)).toBeVisible();
    await expect(
      page.getByTestId('conversations-group-today').getByTestId(`conversation-thread-${secondId}`),
    ).toHaveCount(0);
    const sidebar = page.getByTestId('desktop-sidebar');
    await expect(sidebar.getByText('Pinned', { exact: true })).toBeVisible();
    await expect(sidebar.locator(`a[href="/chat/${secondId}"]`)).toBeVisible();

    await page.getByTestId('conversations-search').fill('Renamed Smoke');
    await expect(page.getByTestId(`conversation-thread-${firstId}`)).toBeVisible();
    await expect(page.getByTestId(`conversation-thread-${secondId}`)).toHaveCount(0);
    await page.getByTestId('conversations-search').fill('');

    await page.evaluate(() => {
      (window as unknown as { confirm: () => boolean }).confirm = () => true;
    });
    await page.getByTestId(`conversation-thread-${firstId}`)
      .getByRole('button', { name: /Delete / })
      .click();
    await expect(page.getByTestId(`conversation-thread-${firstId}`)).toHaveCount(0, { timeout: 5_000 });

    await expect(page.getByTestId(`conversation-thread-${secondId}`)).toBeVisible();

    // Row management remains operable without hover and does not overflow a
    // phone-sized viewport.
    await page.setViewportSize({ width: 390, height: 844 });
    const unpin = page.getByTestId(`conversation-thread-${secondId}`)
      .getByRole('button', { name: /Unpin / });
    await expect(unpin).toBeVisible();
    await unpin.focus();
    await expect(unpin).toBeFocused();
    const widths = await page.evaluate(() => ({
      viewport: window.innerWidth,
      document: document.documentElement.scrollWidth,
    }));
    expect(widths.document).toBeLessThanOrEqual(widths.viewport);
  });
});
