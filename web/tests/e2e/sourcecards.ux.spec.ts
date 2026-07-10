import { expect, test, type BrowserContext, type Page } from '@playwright/test';

const richSources = [
  {
    url: 'https://example.com/olympia-restaurants',
    title: '4 new Olympia restaurants: hours, locations, signature food',
    domain: 'theolympian.com',
    excerpt: 'Olympia dining keeps expanding with new Mexican fare, breakfast sandwiches, and Japanese-style donuts.',
    favicon: iconDataUrl('O', '#c79239'),
    thumbnail: thumbDataUrl('OLYMPIA FOOD', 'New restaurants downtown', '#1b3f6e', '#102030'),
    publishedAt: '2026-05-10T08:00:00Z',
  },
  {
    url: 'https://example.org/kawaii-donut',
    title: "Downtown Olympia's Kawaii Donut House kicks off a sweet spring",
    domain: 'thurstontalk.com',
    excerpt: 'A local donut shop is drawing attention for soft mochi donuts and weekend breakfast traffic.',
    favicon: iconDataUrl('T', '#3b82f6'),
    thumbnail: thumbDataUrl('KAWAII DONUT', 'Mochi donuts and morning lines', '#6f4d18', '#c9973e'),
    publishedAt: '2026-05-09T15:30:00Z',
  },
  {
    url: 'https://example.net/glowies-breakfast',
    title: "Glowies adds breakfast sandwiches and baked goods near Olympia's core",
    domain: 'olyfed.com',
    excerpt: 'The bakery cafe is pairing breakfast sandwiches with pastries in a small downtown space.',
    favicon: iconDataUrl('G', '#10b981'),
    thumbnail: thumbDataUrl('GLOWIES', 'Breakfast sandwiches and pastries', '#24394f', '#9a702f'),
    publishedAt: '2026-05-08T18:45:00Z',
  },
  {
    url: 'https://example.edu/portland-concerts',
    title: 'Concerts and events in Portland this week',
    domain: 'bandsintown.com',
    excerpt: 'Venue calendars point to a busy week for small stages, touring acts, and last-minute ticket listings.',
    favicon: iconDataUrl('B', '#58718d'),
    thumbnail: thumbDataUrl('LIVE MUSIC', 'Concerts and small venues', '#1c2e43', '#58718d'),
    publishedAt: '2026-05-07T19:00:00Z',
  },
  {
    url: 'https://example.dev/weather-alert',
    title: '7-day forecast update includes cool mornings and lighter wind',
    domain: 'forecast.weather.gov',
    excerpt: 'The latest forecast emphasizes mild temperatures, patchy clouds, and a calmer weekend pattern.',
    favicon: iconDataUrl('W', '#06b6d4'),
    thumbnail: thumbDataUrl('FORECAST', 'Cool mornings and calmer wind', '#0c5962', '#1b3f6e'),
    publishedAt: '2026-05-10T12:00:00Z',
  },
];

test.describe('source cards UX', () => {
  test.setTimeout(90_000);

  test('rich source cards are polished across desktop and mobile', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl).toBeTruthy();
    expect(token).toBeTruthy();

    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });
    await forceDarkTheme(context);
    await installRoutes(page, baseUrl!);

    const desktopScore = await inspectAtViewport(page, baseUrl!, 1224, 900, 'desktop');
    expect(desktopScore, JSON.stringify(desktopScore.checks)).toMatchObject({ total: 10 });

    const mobileScore = await inspectAtViewport(page, baseUrl!, 390, 780, 'mobile');
    expect(mobileScore, JSON.stringify(mobileScore.checks)).toMatchObject({ total: 10 });
  });
});

async function inspectAtViewport(
  page: Page,
  baseUrl: string,
  width: number,
  height: number,
  label: string,
): Promise<{ total: number; checks: Record<string, boolean> }> {
  await page.setViewportSize({ width, height });
  await page.goto(`${baseUrl}/chat`, { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('route-chat')).toBeVisible();
  await page.getByTestId('chat-thread-source-cards-ux').click();
  await expect(page.getByTestId('route-chat-thread')).toBeVisible();
  await expect(page.getByTestId('chat-source-cards')).toBeVisible();

  const cards = page.getByTestId('chat-source-cards').locator('[data-source-card="true"]');
  await expect(cards).toHaveCount(richSources.length);
  await expect(page.getByTestId('source-card-thumbnail')).toHaveCount(richSources.length);
  await expect(page.getByTestId('chat-latest-response-actions')).toBeVisible();
  const sourceImages = page.getByTestId('chat-source-cards').locator('img');
  await expect.poll(() => sourceImages.count()).toBeGreaterThanOrEqual(richSources.length);
  await expect.poll(
    () => sourceImages.evaluateAll((images) =>
      images
        .filter((image) => {
          const rect = image.getBoundingClientRect();
          return rect.bottom > 0 && rect.top < window.innerHeight;
        })
        .every((image) => image.complete && image.naturalWidth > 0)),
  ).toBe(true);

  const checks = await page.evaluate(() => {
    const sourceSection = document.querySelector<HTMLElement>('[data-testid="chat-source-cards"]');
    const sourceCards = Array.from(document.querySelectorAll<HTMLElement>('[data-source-card="true"]'));
    const images = Array.from(sourceSection?.querySelectorAll<HTMLImageElement>('img') ?? []);
    const composer = document.querySelector<HTMLElement>('[data-testid="chat-composer"]');
    const viewportWidth = document.documentElement.clientWidth;
    const sectionRect = sourceSection?.getBoundingClientRect();
    const composerTop = composer?.getBoundingClientRect().top ?? window.innerHeight;
    const visibleCards = sourceCards.every((card) => {
      const rect = card.getBoundingClientRect();
      return rect.width > 120 &&
        rect.height > 96 &&
        rect.left >= -1 &&
        rect.right <= viewportWidth + 1;
    });
    const imagesReady = images
      .filter((image) => {
        const rect = image.getBoundingClientRect();
        return rect.bottom > 0 && rect.top < window.innerHeight;
      })
      .every((image) => image.complete && image.naturalWidth > 0);
    const noHorizontalPageOverflow =
      document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1;
    const noCardOverlap = sourceCards.every((card, index) => {
      const rect = card.getBoundingClientRect();
      return sourceCards.every((other, otherIndex) => {
        if (otherIndex <= index) return true;
        const otherRect = other.getBoundingClientRect();
        const separated =
          rect.right <= otherRect.left + 1 ||
          otherRect.right <= rect.left + 1 ||
          rect.bottom <= otherRect.top + 1 ||
          otherRect.bottom <= rect.top + 1;
        return separated;
      });
    });
    const noComposerOverlap = window.innerWidth < 768
      ? true
      : sourceCards.every((card) => {
          const rect = card.getBoundingClientRect();
          return rect.bottom <= composerTop - 8 || rect.top >= composerTop + 8;
        });

    return {
      sectionVisible: Boolean(sectionRect && sectionRect.width > 240 && sectionRect.height > 200),
      visibleCards,
      imagesReady,
      noHorizontalPageOverflow,
      noCardOverlap,
      noComposerOverlap,
      actionRailVisible: Boolean(document.querySelector('[data-testid="chat-latest-response-actions"]')),
    };
  });

  const total = Math.round(
    (Object.values(checks).filter(Boolean).length / Object.keys(checks).length) * 10,
  );
  if (label === 'desktop') {
    const messageList = page.getByTestId('chat-message-list');
    await messageList.evaluate((element) => {
      element.scrollTo({ top: 0, behavior: 'auto' });
    });
  }
  await page.screenshot({ path: `test-results/sourcecards-ux-${label}.png`, fullPage: true });

  return { total, checks };
}

async function forceDarkTheme(context: BrowserContext) {
  await context.addInitScript(() => {
    window.localStorage.setItem('thaddeus.theme', 'dark');
  });
}

async function installRoutes(page: Page, baseUrl: string) {
  await page.route(`${baseUrl}/api/threads`, async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        threads: [
          {
            id: 'source-cards-ux',
            title: 'Source cards UX',
            createdAt: '2026-05-10T12:00:00Z',
            updatedAt: '2026-05-10T12:01:00Z',
            messageCount: 2,
            lastMessagePreview: 'I pulled together the most relevant current reporting.',
          },
        ],
      }),
    });
  });

  await page.route(`${baseUrl}/api/threads/source-cards-ux`, async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        id: 'source-cards-ux',
        title: 'Source cards UX',
        createdAt: '2026-05-10T12:00:00Z',
        updatedAt: '2026-05-10T12:01:00Z',
        messages: [
          {
            id: 'user-1',
            role: 'user',
            text: 'Can you bring me up some recent local reporting?',
            createdAt: '2026-05-10T12:00:00Z',
          },
          {
            id: 'assistant-1',
            role: 'assistant',
            text: 'Here are the strongest current sources I found.',
            createdAt: '2026-05-10T12:01:00Z',
            sources: richSources,
          },
        ],
      }),
    });
  });
}

function iconDataUrl(letter: string, color: string) {
  return `data:image/svg+xml;utf8,${encodeURIComponent(`
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">
      <rect width="32" height="32" rx="8" fill="${color}"/>
      <text x="16" y="21" text-anchor="middle" font-family="Arial" font-size="16" font-weight="700" fill="white">${letter}</text>
    </svg>
  `)}`;
}

function thumbDataUrl(kicker: string, title: string, start: string, end: string) {
  return `data:image/svg+xml;utf8,${encodeURIComponent(`
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 675">
      <defs>
        <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stop-color="${start}"/>
          <stop offset="100%" stop-color="${end}"/>
        </linearGradient>
      </defs>
      <rect width="1200" height="675" fill="url(#bg)"/>
      <circle cx="1000" cy="110" r="160" fill="rgba(255,255,255,0.14)"/>
      <circle cx="180" cy="620" r="220" fill="rgba(255,255,255,0.10)"/>
      <path d="M0 480 C160 420 305 425 470 480 C650 540 800 535 980 470 C1070 438 1140 436 1200 450 L1200 675 L0 675 Z" fill="rgba(255,255,255,0.14)"/>
      <text x="72" y="102" fill="rgba(255,255,255,0.82)" font-family="Inter, Arial, sans-serif" font-size="34" font-weight="700" letter-spacing="5">${kicker}</text>
      <text x="72" y="180" fill="white" font-family="Inter, Arial, sans-serif" font-size="58" font-weight="700">${title}</text>
    </svg>
  `)}`;
}
