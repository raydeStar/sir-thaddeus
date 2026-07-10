import { expect, test } from '@playwright/test';

const palettes = {
  light: {
    canvas: '#f7f5ef',
    ink: '#17212e',
    accent: '#96651d',
  },
  dark: {
    canvas: '#080d14',
    ink: '#f1ecdf',
    accent: '#d2a34f',
  },
} as const;

test.describe('Sir Thaddeus visual system', () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`${theme} theme uses the midnight and brass palette`, async ({ page, context }) => {
      const baseUrl = process.env.RUNTIME_BASE_URL;
      const token = process.env.RUNTIME_TOKEN;
      expect(baseUrl).toBeTruthy();
      expect(token).toBeTruthy();
      await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });
      await page.addInitScript((preference) => {
        window.localStorage.setItem('thaddeus.theme', preference);
      }, theme);

      await page.goto(`${baseUrl}/`, { waitUntil: 'domcontentloaded' });
      await expect(page.getByTestId('route-home')).toBeVisible();

      const tokens = await page.evaluate(() => {
        const styles = getComputedStyle(document.documentElement);
        return {
          canvas: styles.getPropertyValue('--color-canvas').trim(),
          ink: styles.getPropertyValue('--color-ink').trim(),
          accent: styles.getPropertyValue('--color-accent').trim(),
        };
      });

      expect(tokens).toEqual(palettes[theme]);
      await expect(page.getByTestId('route-home').getByText('Sir Thaddeus', { exact: true })).toBeVisible();
      await expect(page.getByText('Your model, memory, and tools—always within your rules.')).toBeVisible();
    });
  }

  test('mobile shell and settings rail do not create page overflow', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl).toBeTruthy();
    expect(token).toBeTruthy();
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });
    await page.addInitScript(() => {
      window.localStorage.setItem('thaddeus.theme', 'dark');
    });
    await page.setViewportSize({ width: 390, height: 844 });

    await page.goto(`${baseUrl}/settings`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('settings-tabs')).toBeVisible();

    const layout = await page.evaluate(() => {
      const tabs = document.querySelector<HTMLElement>('[data-testid="settings-tabs"]');
      return {
        pageWidth: document.documentElement.scrollWidth,
        viewportWidth: window.innerWidth,
        tabsScrollable: Boolean(tabs && tabs.scrollWidth > tabs.clientWidth),
      };
    });

    expect(layout.pageWidth).toBeLessThanOrEqual(layout.viewportWidth);
    expect(layout.tabsScrollable).toBe(true);
  });
});
