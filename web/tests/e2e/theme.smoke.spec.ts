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

  test('collapsed desktop navigation centers every icon in its target', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl).toBeTruthy();
    expect(token).toBeTruthy();
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });
    await page.setViewportSize({ width: 1224, height: 816 });

    await page.goto(`${baseUrl}/chat`, { waitUntil: 'domcontentloaded' });
    const sidebar = page.getByTestId('desktop-sidebar');
    await expect(sidebar).toBeVisible();

    const alignment = await page.locator('[data-testid^="desktop-nav-"]').evaluateAll((links) =>
      links.map((link) => {
        const icon = link.querySelector('svg');
        const linkRect = link.getBoundingClientRect();
        const iconRect = icon?.getBoundingClientRect();
        return {
          label: link.getAttribute('data-testid'),
          centerDelta: iconRect
            ? Math.abs(
                (iconRect.left + iconRect.width / 2) -
                (linkRect.left + linkRect.width / 2),
              )
            : Number.POSITIVE_INFINITY,
          hiddenLabelWidth: link.querySelector('span')?.getBoundingClientRect().width ?? -1,
        };
      }),
    );

    expect(alignment).toHaveLength(primaryNavCount + secondaryNavCount);
    for (const item of alignment) {
      expect(item.centerDelta, item.label ?? 'navigation item').toBeLessThanOrEqual(0.5);
      expect(item.hiddenLabelWidth, item.label ?? 'navigation item').toBe(0);
    }

    await sidebar.hover();
    await expect.poll(() => sidebar.evaluate((element) =>
      element.getBoundingClientRect().width)).toBeGreaterThanOrEqual(223);
    const visibleLabels = await page.locator('[data-testid^="desktop-nav-"] span').evaluateAll(
      (labels) => labels.filter((label) => label.getBoundingClientRect().width > 0).length,
    );
    expect(visibleLabels).toBe(primaryNavCount + secondaryNavCount);
  });
});

const primaryNavCount = 6;
const secondaryNavCount = 4;
