import { test, expect } from '@playwright/test';

test('user can complete the onboarding wizard', async ({ page, context }) => {
  const baseUrl = process.env.RUNTIME_BASE_URL!;
  const token = process.env.RUNTIME_TOKEN!;
  await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

  await page.goto(`${baseUrl}/onboarding`, { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('route-onboarding')).toBeVisible();
  await expect(page.getByTestId('onboarding-step-welcome')).toBeVisible({ timeout: 10_000 });

  await page.getByTestId('onboarding-next').click();
  await expect(page.getByTestId('onboarding-step-privacy')).toBeVisible();

  await page.getByTestId('onboarding-next').click();
  await expect(page.getByTestId('onboarding-step-voice')).toBeVisible();

  await page.getByTestId('onboarding-next').click();
  await expect(page.getByTestId('onboarding-step-done')).toBeVisible();

  await page.getByTestId('onboarding-finish').click();

  // Lands on the workspace (root).
  await page.waitForURL((u) => u.pathname === '/' || u.pathname === '/index.html', {
    timeout: 5_000,
  });

  // Verify the flag was persisted via the REST surface.
  const res = await page.request.get(`${baseUrl}/api/settings`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  expect(res.ok()).toBeTruthy();
  const settings = await res.json();
  expect(settings.flags?.onboardingCompleted).toBe(true);
});
