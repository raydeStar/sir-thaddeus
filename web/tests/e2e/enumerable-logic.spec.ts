import { test, expect, type Page, type BrowserContext } from '@playwright/test';

/**
 * Exercises deterministic enumerable reasoning through the real chat UI and
 * runtime API. These prompts should terminate in the utility fast path: no web
 * tools and no local model are needed for the assistant to answer correctly.
 */
test.describe('enumerable logic chat e2e', () => {
  test.setTimeout(60_000);

  test('counts the canonical days-of-week set by applying the letter criterion', async ({ page, context }) => {
    const assistantReply = await sendChatPrompt(
      page,
      context,
      'how many days of the week have the letter D in them?',
    );

    await expect(assistantReply).toContainText('7', { timeout: 10_000 });
    await expect(assistantReply).toContainText('Monday');
    await expect(assistantReply).toContainText('Tuesday');
    await expect(assistantReply).toContainText('Wednesday');
    await expect(assistantReply).toContainText('Sunday');
  });

  test('extrapolates a representative open collection in chat', async ({ page, context }) => {
    const assistantReply = await sendChatPrompt(
      page,
      context,
      "Extrapolate the data 'computer parts'",
    );

    await expect(assistantReply).toContainText('representative common set', { timeout: 10_000 });
    await expect(assistantReply).toContainText('not exhaustive');
    await expect(assistantReply).toContainText('processor');
    await expect(assistantReply).toContainText('motherboard');
    await expect(assistantReply).toContainText('screen');
  });
});

async function sendChatPrompt(page: Page, context: BrowserContext, prompt: string) {
  const baseUrl = process.env.RUNTIME_BASE_URL;
  const token = process.env.RUNTIME_TOKEN;
  expect(baseUrl, 'global-setup must populate RUNTIME_BASE_URL').toBeTruthy();
  expect(token, 'global-setup must populate RUNTIME_TOKEN').toBeTruthy();

  await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });
  await page.goto(`${baseUrl}/chat`, { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('route-chat')).toBeVisible();
  await expect(page.getByTestId('runtime-connection-dot')).toHaveAttribute(
    'data-connected',
    'true',
    { timeout: 10_000 },
  );

  await page.getByTestId('chat-new-thread').click();
  await expect(page.getByTestId('route-chat-thread')).toBeVisible({ timeout: 10_000 });

  await page.getByTestId('chat-input').fill(prompt);
  await page.getByTestId('chat-send').click();

  await expect(page.getByTestId('chat-message-list')).toContainText(prompt, { timeout: 10_000 });
  const assistantReply = page
    .getByTestId('chat-message-list')
    .locator('[data-role="assistant"]')
    .last();
  await expect(assistantReply).toBeVisible({ timeout: 10_000 });
  await expect(assistantReply).not.toHaveText(/^\s*$/, { timeout: 10_000 });

  return assistantReply;
}
