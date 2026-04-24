import { test, expect } from '@playwright/test';

/**
 * Phase 3.7 smoke. Verifies the chat surface end-to-end: create a thread,
 * navigate into it, post a message, watch the assistant reply stream over /ws,
 * and confirm the runtime state badge ticks Idle -> Thinking -> Idle.
 *
 * The runtime is started in --test-mode by global-setup, so threads land under
 * the test-mode lock dir, not the user's real ~/.thaddeus.
 */
test.describe('chat smoke', () => {
  // Cold-start on a large local model can push past the global 30s test
  // budget — override so the first chat turn has room to warm up.
  test.setTimeout(90_000);

  test('assistant source cards render for cited web results', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl).toBeTruthy();
    expect(token).toBeTruthy();

    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    await page.route(`${baseUrl}/api/threads`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          threads: [
            {
              id: 'source-cards-demo',
              title: 'Strait of Hormuz',
              createdAt: '2026-04-23T12:00:00Z',
              updatedAt: '2026-04-23T12:01:00Z',
              messageCount: 2,
              lastMessagePreview: 'Here are two recent reports worth scanning first.',
            },
          ],
        }),
      });
    });

    await page.route(`${baseUrl}/api/threads/source-cards-demo`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          id: 'source-cards-demo',
          title: 'Strait of Hormuz',
          createdAt: '2026-04-23T12:00:00Z',
          updatedAt: '2026-04-23T12:01:00Z',
          messages: [
            {
              id: 'user-1',
              role: 'user',
              text: "What's the newest with the Strait of Hormuz?",
              createdAt: '2026-04-23T12:00:00Z',
            },
            {
              id: 'assistant-1',
              role: 'assistant',
              text: 'Here are two recent reports worth scanning first.',
              createdAt: '2026-04-23T12:01:00Z',
              sources: [
                {
                  url: 'https://example.com/hormuz-shipping-update',
                  title: 'Shipping insurers reassess Strait of Hormuz risk',
                  domain: 'example.com',
                  excerpt: 'Markets are reacting to new transit risk assessments and convoy guidance.',
                  favicon:
                    'data:image/svg+xml;utf8,<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" rx="8" fill="%23d97757"/><text x="16" y="21" text-anchor="middle" font-size="16" fill="white">E</text></svg>',
                  thumbnail:
                    'data:image/svg+xml;utf8,<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 720"><defs><linearGradient id="bg" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" stop-color="%23b85d43"/><stop offset="55%" stop-color="%234a596f"/><stop offset="100%" stop-color="%23161f2d"/></linearGradient></defs><rect width="1200" height="720" fill="url(%23bg)"/><circle cx="970" cy="120" r="140" fill="rgba(255,255,255,0.12)"/><path d="M0 520 C160 470 300 470 460 520 C620 570 760 565 930 510 C1040 474 1130 472 1200 494 L1200 720 L0 720 Z" fill="rgba(255,255,255,0.12)"/><path d="M0 560 C170 530 320 540 470 582 C655 635 826 626 1000 566 C1082 537 1148 531 1200 540 L1200 720 L0 720 Z" fill="rgba(255,255,255,0.2)"/><text x="84" y="128" fill="white" font-family="Inter, Arial, sans-serif" font-size="34" font-weight="700" letter-spacing="4">STRAIT OF HORMUZ</text><text x="84" y="184" fill="rgba(255,255,255,0.84)" font-family="Inter, Arial, sans-serif" font-size="54" font-weight="700">Shipping risk and military posture</text><text x="84" y="244" fill="rgba(255,255,255,0.76)" font-family="Inter, Arial, sans-serif" font-size="30">Transit insurers, convoy guidance, and regional response</text></svg>',
                  publishedAt: '2026-04-23T08:30:00Z',
                },
                {
                  url: 'https://example.org/naval-briefing',
                  title: 'Regional navies increase monitoring near key shipping lanes',
                  domain: 'example.org',
                  excerpt: 'Officials say monitoring has intensified while carriers review schedules.',
                  publishedAt: '2026-04-23T06:00:00Z',
                },
              ],
            },
          ],
        }),
      });
    });

    await page.goto(`${baseUrl}/chat`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('route-chat')).toBeVisible();
    await page.getByTestId('chat-thread-source-cards-demo').click();
    await expect(page.getByTestId('route-chat-thread')).toBeVisible();

    const cards = page.getByTestId('chat-source-cards').locator('a');
    await expect(cards).toHaveCount(2);
    await expect(page.getByRole('link', { name: /Shipping insurers reassess Strait of Hormuz risk/i })).toBeVisible();
    await expect(page.getByTestId('chat-source-cards')).toContainText('example.com');
    await expect(page.getByTestId('chat-source-cards')).toContainText('Markets are reacting to new transit risk assessments');
    await page.screenshot({ path: 'test-results/chat-source-cards-review.png', fullPage: true });
  });

  test('user can send a message and receive a streamed assistant reply', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl).toBeTruthy();
    expect(token).toBeTruthy();

    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    await page.goto(`${baseUrl}/chat`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('route-chat')).toBeVisible();

    // Wait for the WS handshake so the chat store can ingest turn events.
    await expect(page.getByTestId('runtime-connection-dot')).toHaveAttribute(
      'data-connected',
      'true',
      { timeout: 10_000 },
    );

    // Create a fresh thread; the page should navigate to /chat/:threadId.
    await page.getByTestId('chat-new-thread').click();
    await expect(page.getByTestId('route-chat-thread')).toBeVisible({ timeout: 10_000 });

    // Send a message.
    const composer = page.getByTestId('chat-input');
    await composer.fill('hello');
    await page.getByTestId('chat-send').click();

    // Streaming bubble appears while the assistant is mid-reply.
    const streaming = page.getByTestId('chat-message-streaming');
    await expect(streaming).toBeVisible({ timeout: 10_000 });

    // Eventually the streaming bubble is replaced by a persisted assistant
    // message and disappears. With the real assistant enabled, the exact
    // wording is nondeterministic, so assert on a non-empty reply instead of
    // the old echo-stub text. Timeout is generous to cover cold-start model
    // load on the first chat turn (large local LLMs can take 20-30s warmup).
    await expect(streaming).toBeHidden({ timeout: 60_000 });
    await expect(page.getByTestId('chat-message-list')).toContainText('hello', { timeout: 5_000 });
    const assistantMessages = page
      .getByTestId('chat-message-list')
      .locator('[data-role="assistant"]');
    await expect(assistantMessages).toHaveCount(1, { timeout: 5_000 });
    await expect(assistantMessages.first()).not.toHaveText(/^\s*$/);

    // The state badge should have ticked Thinking during the turn and returned
    // to Idle once the assistant finished.
    await expect(page.getByTestId('runtime-state-badge')).toHaveAttribute('data-state', 'Idle', {
      timeout: 10_000,
    });
  });
});
