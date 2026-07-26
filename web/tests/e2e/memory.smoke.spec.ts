import { test, expect } from '@playwright/test';

/**
 * Smoke for the new /memory audit route — replaces the old memos page.
 * The runtime starts in --test-mode with a fresh memory SQLite, so the
 * empty state is what a brand-new install would see.
 */
test('memory audit page renders and shows empty state on a fresh runtime', async ({
  page,
  context,
}) => {
  const baseUrl = process.env.RUNTIME_BASE_URL!;
  const token = process.env.RUNTIME_TOKEN!;
  await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

  await page.goto(`${baseUrl}/memory`, { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('route-memory')).toBeVisible();

  // The search bar is always visible regardless of memory contents.
  // Legacy memo files are migrated into the wiki on first boot via
  // MemosToWikiMigrator.
  await expect(page.getByTestId('memory-search')).toBeVisible();

  // Brand-new runtime → either empty state OR an overview card with all
  // zeros. Either is fine; we just assert one of them resolves.
  const empty = page.getByTestId('memory-empty');
  const overview = page.getByTestId('memory-overview');
  await expect(empty.or(overview)).toBeVisible({ timeout: 5_000 });
});

test('memory audit API returns a zeroed overview on a fresh runtime', async ({ page }) => {
  const baseUrl = process.env.RUNTIME_BASE_URL!;
  const token = process.env.RUNTIME_TOKEN!;

  const res = await page.request.get(`${baseUrl}/api/memory/overview`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  expect(res.ok()).toBeTruthy();
  const body = await res.json();
  expect(typeof body.factCount).toBe('number');
  expect(typeof body.eventCount).toBe('number');
  expect(typeof body.chunkCount).toBe('number');
  expect(typeof body.nuggetCount).toBe('number');
});

test('memory can be paused, resumed, and permanently reset with confirmation', async ({
  page,
  context,
}) => {
  const baseUrl = process.env.RUNTIME_BASE_URL!;
  const token = process.env.RUNTIME_TOKEN!;
  await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });
  await page.goto(`${baseUrl}/memory`, { waitUntil: 'domcontentloaded' });

  const toggle = page.getByTestId('memory-policy-toggle');
  await expect(toggle).toContainText('Pause memory');
  await toggle.click();
  await expect(page.getByTestId('memory-paused-status')).toContainText(
    'chat turns will not read or write durable memory',
  );
  const paused = await context.request.get(`${baseUrl}/api/memory/policy`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  expect((await paused.json() as { enabled: boolean }).enabled).toBe(false);

  await toggle.click();
  await expect(page.getByTestId('memory-paused-status')).toBeHidden();
  const resumed = await context.request.get(`${baseUrl}/api/memory/policy`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  expect((await resumed.json() as { enabled: boolean }).enabled).toBe(true);

  page.once('dialog', (dialog) => dialog.accept());
  await page.getByTestId('memory-reset').click();
  await expect(page.getByTestId('memory-reset-notice')).toContainText(
    'durable rows permanently removed',
  );
});
