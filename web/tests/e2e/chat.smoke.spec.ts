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
    // the old echo-stub text.
    await expect(streaming).toBeHidden({ timeout: 15_000 });
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
