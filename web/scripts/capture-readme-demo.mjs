import { chromium } from '@playwright/test';
import { console } from 'node:console';
import { copyFile, mkdir, rm } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const baseUrl = process.env.RUNTIME_BASE_URL?.replace(/\/$/, '');
const token = process.env.RUNTIME_TOKEN;
if (!baseUrl || !token) {
  throw new Error('Set RUNTIME_BASE_URL and RUNTIME_TOKEN before capturing the README demo.');
}

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '../..');
const videoDir = path.join(repoRoot, 'web', 'test-results', 'readme-demo-video');
const outputPath = path.join(repoRoot, 'assets', 'images', 'permission-flow-demo.webm');
await rm(videoDir, { recursive: true, force: true });
await mkdir(videoDir, { recursive: true });

let releasePending;
const pendingReady = new Promise((resolve) => { releasePending = resolve; });
const permissions = [
  {
    id: 'readme_geo',
    tool: 'weather_geocode',
    group: 'Web',
    argsJson: '{"place":"Olympia, Washington"}',
    threadId: 'readme-demo',
    turnId: 'readme-turn',
    createdAt: '2026-07-09T12:00:00Z',
    scope: 'tool',
  },
  {
    id: 'readme_search',
    tool: 'web_search',
    group: 'Web',
    argsJson: '{"query":"Olympia WA weather and downtown events today"}',
    threadId: 'readme-demo',
    turnId: 'readme-turn',
    createdAt: '2026-07-09T12:00:01Z',
    scope: 'tool',
  },
];

const sources = [
  source(
    'forecast.weather.gov',
    'Olympia forecast: mostly clear with a high near 79°F',
    'FORECAST',
    'A calm summer afternoon',
    '#155e75',
    '#2563eb',
  ),
  source(
    'wta.org',
    'Olympia Farmers Market opens for the downtown weekend',
    'DOWNTOWN',
    'Market stalls and local food',
    '#92400e',
    '#d97757',
  ),
  source(
    'experienceolympia.com',
    'Waterfront music and evening events around the harbor',
    'EVENTS',
    'An evening by the water',
    '#4c1d95',
    '#7c3aed',
  ),
];

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({
  viewport: { width: 1224, height: 816 },
  colorScheme: 'dark',
  extraHTTPHeaders: { Authorization: `Bearer ${token}` },
  recordVideo: { dir: videoDir, size: { width: 1224, height: 816 } },
});
await context.addInitScript(() => {
  globalThis.localStorage.setItem('thaddeus.theme', 'dark');
});

const page = await context.newPage();
await page.route(`${baseUrl}/api/permissions/pending`, async (route) => {
  await pendingReady;
  await route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ requests: permissions }),
  });
});
await page.route(`${baseUrl}/api/permissions/respond`, async (route) => {
  await route.fulfill({ contentType: 'application/json', body: '{}' });
});
await page.route(`${baseUrl}/api/threads`, async (route) => {
  await route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      threads: [{
        id: 'readme-demo',
        title: 'Weather + downtown Olympia',
        createdAt: '2026-07-09T12:00:00Z',
        updatedAt: '2026-07-09T12:01:00Z',
        messageCount: 2,
        lastMessagePreview: 'Mostly clear, with several strong downtown leads.',
      }],
    }),
  });
});
await page.route(`${baseUrl}/api/threads/readme-demo`, async (route) => {
  await route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      id: 'readme-demo',
      title: 'Weather + downtown Olympia',
      createdAt: '2026-07-09T12:00:00Z',
      updatedAt: '2026-07-09T12:01:00Z',
      messages: [
        {
          id: 'readme-user',
          role: 'user',
          text: "What's the weather like in Olympia today, and is anything happening downtown?",
          createdAt: '2026-07-09T12:00:00Z',
        },
        {
          id: 'readme-assistant',
          role: 'assistant',
          text: 'Mostly clear and 72°F in Olympia. Downtown, the strongest current leads are the farmers market and waterfront music this evening.',
          createdAt: '2026-07-09T12:01:00Z',
          sources,
        },
      ],
    }),
  });
});

const video = page.video();
const captureStartedAt = Date.now();
await page.goto(`${baseUrl}/`, { waitUntil: 'domcontentloaded' });
await page.getByTestId('route-home').waitFor({ state: 'visible' });
await page.getByRole('link', { name: 'Chat', exact: true }).click();
await page.getByTestId('route-chat').waitFor({ state: 'visible' });
await page.getByTestId('chat-thread-readme-demo').click();
await page.getByTestId('route-chat-thread').waitFor({ state: 'visible' });
releasePending();

const modal = page.getByTestId('permission-modal');
await modal.waitFor({ state: 'visible' });
const demoStartSeconds = (Date.now() - captureStartedAt) / 1000;
await page.waitForTimeout(1_200);

await page.getByTestId('permission-once').click();
await page.getByTestId('permission-modal-tool').filter({ hasText: 'web_search' }).waitFor({ state: 'visible' });
await page.waitForTimeout(1_400);

await page.getByTestId('permission-once').click();
await modal.waitFor({ state: 'hidden' });
await page.getByTestId('chat-source-cards').waitFor({ state: 'visible' });
await page.waitForTimeout(2_600);

await context.close();
await browser.close();
const recordedPath = await video.path();
await copyFile(recordedPath, outputPath);
console.log(JSON.stringify({ outputPath, demoStartSeconds }));

function source(domain, title, kicker, visualTitle, start, end) {
  return {
    url: `https://${domain}/readme-demo`,
    title,
    domain,
    excerpt: title,
    favicon: iconDataUrl(domain[0].toUpperCase(), start),
    thumbnail: thumbDataUrl(kicker, visualTitle, start, end),
    publishedAt: '2026-07-09T10:00:00Z',
  };
}

function iconDataUrl(letter, color) {
  return `data:image/svg+xml;utf8,${encodeURIComponent(`
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">
      <rect width="32" height="32" rx="8" fill="${color}"/>
      <text x="16" y="21" text-anchor="middle" font-family="Arial" font-size="16" font-weight="700" fill="white">${letter}</text>
    </svg>
  `)}`;
}

function thumbDataUrl(kicker, title, start, end) {
  return `data:image/svg+xml;utf8,${encodeURIComponent(`
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 675">
      <defs><linearGradient id="bg" x1="0" y1="0" x2="1" y2="1"><stop stop-color="${start}"/><stop offset="1" stop-color="${end}"/></linearGradient></defs>
      <rect width="1200" height="675" fill="url(#bg)"/>
      <circle cx="1000" cy="110" r="160" fill="rgba(255,255,255,.14)"/>
      <path d="M0 480C170 420 330 430 500 490 680 552 840 530 1200 440V675H0Z" fill="rgba(255,255,255,.13)"/>
      <text x="72" y="102" fill="rgba(255,255,255,.82)" font-family="Arial" font-size="34" font-weight="700" letter-spacing="5">${kicker}</text>
      <text x="72" y="180" fill="white" font-family="Arial" font-size="58" font-weight="700">${title}</text>
    </svg>
  `)}`;
}
