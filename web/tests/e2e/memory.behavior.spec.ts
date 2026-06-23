import { test, expect } from '@playwright/test';

type MemoryFixture = {
  overview: {
    factCount: number;
    eventCount: number;
    chunkCount: number;
    nuggetCount: number;
    profile: null;
  };
  facts: Array<{
    id: string;
    subject: string;
    predicate: string;
    object: string;
    confidence: number;
    weight: number;
    sensitivity: string;
    createdAt: string;
    updatedAt: string;
    origin?: string | null;
    profileId?: string | null;
    sourceTurnId?: string | null;
    sourceRef?: string | null;
  }>;
  nuggets: Array<{
    id: string;
    text: string;
    tags?: string | null;
    pinned: boolean;
    pinLevel: number;
    weight: number;
    sensitivity: string;
    useCount: number;
    lastUsedAt?: string | null;
    createdAt: string;
    updatedAt: string;
    origin?: string | null;
    sourceTurnId?: string | null;
  }>;
};

test.describe('memory correction and provenance', () => {
  test('user can edit a fact and note from the memory page', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL!;
    const token = process.env.RUNTIME_TOKEN!;
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    const fixture = createMemoryFixture();
    let factUpdateBody: Record<string, unknown> | null = null;
    let nuggetUpdateBody: Record<string, unknown> | null = null;

    await mockMemorySurface(page, baseUrl, fixture);

    await page.route(`${baseUrl}/api/memory/facts/fact-1`, async (route) => {
      expect(route.request().method()).toBe('PUT');
      factUpdateBody = route.request().postDataJSON() as Record<string, unknown>;
      fixture.facts = [
        {
          ...fixture.facts[0],
          subject: String(factUpdateBody.subject),
          predicate: String(factUpdateBody.predicate),
          object: String(factUpdateBody.object),
          updatedAt: '2026-05-11T12:05:00Z',
        },
      ];
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify(fixture.facts[0]),
      });
    });

    await page.route(`${baseUrl}/api/memory/nuggets/nugget-1`, async (route) => {
      expect(route.request().method()).toBe('PUT');
      nuggetUpdateBody = route.request().postDataJSON() as Record<string, unknown>;
      fixture.nuggets = [
        {
          ...fixture.nuggets[0],
          text: String(nuggetUpdateBody.text),
          tags: ';lighting;focus;',
          updatedAt: '2026-05-11T12:06:00Z',
        },
      ];
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify(fixture.nuggets[0]),
      });
    });

    await page.goto(`${baseUrl}/memory`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('route-memory')).toBeVisible();

    const factRow = page.getByTestId('memory-fact-fact-1');
    await factRow.getByTestId('memory-fact-edit-fact-1').click();
    await factRow.getByTestId('memory-fact-edit-subject-fact-1').fill('user');
    await factRow.getByTestId('memory-fact-edit-predicate-fact-1').fill('prefers');
    await factRow.getByTestId('memory-fact-edit-object-fact-1').fill('earl grey');
    await factRow.getByTestId('memory-fact-save-fact-1').click();

    await expect(factRow).toContainText('user');
    await expect(factRow).toContainText('prefers');
    await expect(factRow).toContainText('earl grey');
    expect(factUpdateBody).toEqual({
      subject: 'user',
      predicate: 'prefers',
      object: 'earl grey',
    });

    const nuggetRow = page.getByTestId('memory-nugget-nugget-1');
    await nuggetRow.getByTestId('memory-nugget-edit-nugget-1').click();
    await nuggetRow.getByTestId('memory-nugget-edit-text-nugget-1').fill('User prefers the office lamp dimmed.');
    await nuggetRow.getByTestId('memory-nugget-edit-tags-nugget-1').fill('lighting, focus');
    await nuggetRow.getByTestId('memory-nugget-save-nugget-1').click();

    await expect(nuggetRow).toContainText('User prefers the office lamp dimmed.');
    await expect(nuggetRow).toContainText('#lighting');
    await expect(nuggetRow).toContainText('#focus');
    expect(nuggetUpdateBody).toEqual({
      text: 'User prefers the office lamp dimmed.',
      tags: 'lighting, focus',
      tagsProvided: true,
    });
  });

  test('open source navigates to the chat turn and highlights the referenced message', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL!;
    const token = process.env.RUNTIME_TOKEN!;
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    const fixture = createMemoryFixture();
    await mockMemorySurface(page, baseUrl, fixture);

    await page.route(`${baseUrl}/api/turns/msg-source-1/trace`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          messageId: 'msg-source-1',
          events: [
            {
              type: 'chat.turn.start',
              correlationId: 'msg-source-1',
              payload: {
                threadId: 'thread-memory-source',
                messageId: 'msg-source-1',
                startedAt: '2026-05-11T11:58:00Z',
              },
            },
          ],
        }),
      });
    });

    await page.route(`${baseUrl}/api/threads/thread-memory-source`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          id: 'thread-memory-source',
          title: 'Memory source thread',
          createdAt: '2026-05-11T11:57:00Z',
          updatedAt: '2026-05-11T11:59:00Z',
          messages: [
            {
              id: 'msg-user-1',
              role: 'user',
              text: 'Please remember that I prefer earl grey and dim lighting.',
              createdAt: '2026-05-11T11:57:30Z',
            },
            {
              id: 'msg-source-1',
              role: 'assistant',
              text: 'Understood. I will keep that preference in mind.',
              createdAt: '2026-05-11T11:58:00Z',
              sources: [],
            },
          ],
        }),
      });
    });

    await page.goto(`${baseUrl}/memory`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('route-memory')).toBeVisible();

    const factRow = page.getByTestId('memory-fact-fact-1');
    await factRow.getByRole('button', { name: 'Open source' }).click();

    await expect(page).toHaveURL(/\/chat\/thread-memory-source\?focusMessageId=msg-source-1/);
    await expect(page.getByTestId('route-chat-thread')).toBeVisible();
    await expect(page.getByTestId('chat-message-msg-source-1')).toHaveClass(/ring-1/);
  });
});

function createMemoryFixture(): MemoryFixture {
  return {
    overview: {
      factCount: 1,
      eventCount: 0,
      chunkCount: 0,
      nuggetCount: 1,
      profile: null,
    },
    facts: [
      {
        id: 'fact-1',
        subject: 'user',
        predicate: 'likes',
        object: 'coffee',
        confidence: 0.92,
        weight: 0.65,
        sensitivity: 'public',
        createdAt: '2026-05-11T11:50:00Z',
        updatedAt: '2026-05-11T11:50:00Z',
        origin: 'user_auto_extract',
        profileId: 'user',
        sourceTurnId: 'msg-source-1',
        sourceRef: 'conv:thread-memory-source',
      },
    ],
    nuggets: [
      {
        id: 'nugget-1',
        text: 'User prefers the desk lamp on.',
        tags: ';lighting;routine;',
        pinned: false,
        pinLevel: 0,
        weight: 0.7,
        sensitivity: 'low',
        useCount: 2,
        lastUsedAt: '2026-05-11T11:53:00Z',
        createdAt: '2026-05-11T11:51:00Z',
        updatedAt: '2026-05-11T11:51:00Z',
        origin: 'user_auto_extract',
        sourceTurnId: 'msg-source-1',
      },
    ],
  };
}

async function mockMemorySurface(page: Parameters<typeof test>[0]['page'], baseUrl: string, fixture: MemoryFixture) {
  await page.route(`${baseUrl}/api/memory/overview`, async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify(fixture.overview),
    });
  });

  await page.route(new RegExp(`${escapeRegExp(baseUrl)}/api/memory/nuggets(\\?.*)?$`), async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ items: fixture.nuggets, totalCount: fixture.nuggets.length }),
    });
  });

  await page.route(new RegExp(`${escapeRegExp(baseUrl)}/api/memory/facts(\\?.*)?$`), async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ items: fixture.facts, totalCount: fixture.facts.length }),
    });
  });

  await page.route(new RegExp(`${escapeRegExp(baseUrl)}/api/memory/events(\\?.*)?$`), async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ items: [], totalCount: 0 }),
    });
  });
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}