import { test, expect } from '@playwright/test';

/**
 * Phase 5.4 smoke. Verifies the activity log surface end-to-end: send a chat
 * message, then navigate to /activity and confirm a row appears for the turn,
 * eventually transitions from Running -> Ok via the activity.* WS events, and
 * the detail view renders the entry.
 *
 * Also smoke-tests /diagnostics by asserting the runtime status panel renders.
 */
test.describe('activity smoke', () => {
  // The first chat turn in a fresh runtime can include model warmup on a
  // large local LLM, which pushes past the default 30s budget. Give the
  // turn-completion assertions below enough room.
  test.setTimeout(90_000);

  test('chat turn appears in the activity log and reaches Ok', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl).toBeTruthy();
    expect(token).toBeTruthy();

    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    // Send a chat message first so an activity entry exists.
    await page.goto(`${baseUrl}/chat`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('runtime-connection-dot')).toHaveAttribute(
      'data-connected',
      'true',
      { timeout: 10_000 },
    );
    await page.getByTestId('chat-new-thread').click();
    await expect(page.getByTestId('route-chat-thread')).toBeVisible({ timeout: 10_000 });
    await page.getByTestId('chat-input').fill('activity smoke');
    await page.getByTestId('chat-send').click();
    await expect(page.getByTestId('chat-message-list')).toContainText('activity smoke', {
      timeout: 10_000,
    });

    // Navigate to the activity log and wait for the row.
    await page.goto(`${baseUrl}/activity`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('route-activity')).toBeVisible();
    const list = page.getByTestId('activity-list');
    await expect(list).toBeVisible({ timeout: 10_000 });

    // First row should be a ChatTurn that ends up Ok. The activity row
    // flips from Running -> Ok when the assistant finishes — on a cold
    // local LLM this can take much longer than 15s, so we give it room.
    const firstRow = list.locator('li').first();
    await expect(firstRow).toHaveAttribute('data-kind', 'ChatTurn');
    await expect(firstRow).toHaveAttribute('data-status', 'Ok', { timeout: 60_000 });

    // Drill into the detail view and confirm the metadata renders.
    await firstRow.locator('a').click();
    console.log('after click url:', page.url());
    console.log('PAGE_CONTENT_START');
    console.log(await page.content());
    console.log('PAGE_CONTENT_END');
    await expect(page.getByTestId('route-activity-entry')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('activity-entry-detail')).toBeVisible();
    await expect(page.getByTestId('activity-entry-kind')).toHaveText('ChatTurn');
    await expect(page.getByTestId('activity-entry-status')).toHaveText('Ok');
    await expect(page.getByTestId('activity-entry-thread-link')).toBeVisible();
  });

  test('diagnostics page renders runtime status', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    await page.goto(`${baseUrl}/diagnostics`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('route-diagnostics')).toBeVisible();
    await expect(page.getByTestId('diagnostics-detail')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('diagnostics-state')).toBeVisible();
    await expect(page.getByTestId('diagnostics-uptime')).toBeVisible();
    await expect(page.getByTestId('diagnostics-pid')).not.toBeEmpty();
  });
});

