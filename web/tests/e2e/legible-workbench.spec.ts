import { expect, test } from '@playwright/test';

test.describe('legible workbench UX', () => {
  test('multi-step work is editable and cannot execute before plan approval', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL!;
    const token = process.env.RUNTIME_TOKEN!;
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    const createdResponse = await context.request.post(`${baseUrl}/api/threads`, {
      headers: { Authorization: `Bearer ${token}` },
      data: { title: 'Plan approval proof' },
    });
    expect(createdResponse.ok()).toBeTruthy();
    const thread = await createdResponse.json() as { id: string };

    await page.goto(`${baseUrl}/chat/${thread.id}`, { waitUntil: 'domcontentloaded' });
    await page.getByTestId('chat-input').fill(
      'Review these local notes, summarize them, and save a brief to the Wiki: alpha, beta, gamma.',
    );
    await page.getByTestId('chat-send').click();

    const plan = page.getByTestId('plan-approval-card');
    await expect(plan).toBeVisible();
    await expect(plan).toContainText('Review the plan before work begins');
    await expect(page.getByTestId('chat-input')).toBeDisabled();

    // This wait is deliberate: the background assistant task has been queued,
    // but the server-side approval signal must keep it from publishing a turn.
    await page.waitForTimeout(350);
    await expect(page.getByTestId('chat-message-streaming')).toHaveCount(0);
    const beforeApproval = await context.request.get(
      `${baseUrl}/api/runs?threadId=${encodeURIComponent(thread.id)}`,
      { headers: { Authorization: `Bearer ${token}` } },
    );
    const beforeBody = await beforeApproval.json() as {
      runs: Array<{ runId: string; state: string; plan: { version: number } }>;
    };
    expect(beforeBody.runs[0].state.toLowerCase()).toBe('awaitingapproval');
    expect(beforeBody.runs[0].plan.version).toBe(1);

    await plan.getByTestId('plan-edit').click();
    const firstStep = plan.getByRole('textbox', { name: 'Step 1', exact: true });
    await firstStep.fill('Review only the explicitly requested local context');
    await plan.getByRole('button', { name: 'Move step 1 down' }).click();
    await plan.getByRole('button', { name: 'Save changes' }).click();
    await expect(plan).toContainText('Review only the explicitly requested local context');

    const revised = await context.request.get(
      `${baseUrl}/api/runs/${encodeURIComponent(beforeBody.runs[0].runId)}`,
      { headers: { Authorization: `Bearer ${token}` } },
    );
    const revisedBody = await revised.json() as {
      state: string;
      plan: { version: number; steps: Array<{ label: string }> };
    };
    expect(revisedBody.state.toLowerCase()).toBe('awaitingapproval');
    expect(revisedBody.plan.version).toBe(2);
    expect(revisedBody.plan.steps[1].label).toBe(
      'Review only the explicitly requested local context',
    );

    await plan.getByTestId('plan-approve').click();
    await expect(plan).toBeHidden();
    await expect(page.getByTestId('chat-input')).toBeEnabled();

    await expect.poll(async () => {
      // A real runtime may legitimately pause on the next least-privilege
      // tool boundary (for example memory retrieval). Resolve that separate
      // contract so this test remains about plan execution, not permission
      // defaults left by another smoke scenario.
      const pendingResponse = await context.request.get(`${baseUrl}/api/permissions/pending`, {
        headers: { Authorization: `Bearer ${token}` },
      });
      const pending = await pendingResponse.json() as {
        requests: Array<{ id: string; scope?: string }>;
      };
      for (const request of pending.requests) {
        await context.request.post(`${baseUrl}/api/permissions/respond`, {
          headers: { Authorization: `Bearer ${token}` },
          data: { id: request.id, decision: 'once', scope: request.scope ?? 'group' },
        });
      }
      const response = await context.request.get(`${baseUrl}/api/threads/${thread.id}`, {
        headers: { Authorization: `Bearer ${token}` },
      });
      const body = await response.json() as { messages: Array<{ role: string }> };
      return body.messages.filter((message) => message.role.toLowerCase() === 'assistant').length;
    }, { timeout: 20_000 }).toBe(1);
  });

  test('incognito is explicit in the composer and reaches the server as a turn policy', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL!;
    const token = process.env.RUNTIME_TOKEN!;
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });
    const createdResponse = await context.request.post(`${baseUrl}/api/threads`, {
      headers: { Authorization: `Bearer ${token}` },
      data: { title: 'Incognito proof' },
    });
    const thread = await createdResponse.json() as { id: string };
    let submitted: { ephemeralMemory?: boolean } | null = null;
    await page.route(
      `${baseUrl}/api/threads/${encodeURIComponent(thread.id)}/messages`,
      async (route) => {
        submitted = route.request().postDataJSON() as { ephemeralMemory?: boolean };
        await route.continue();
      },
    );

    await page.goto(`${baseUrl}/chat/${thread.id}`, { waitUntil: 'domcontentloaded' });
    await page.getByTestId('chat-incognito-toggle').click();
    await expect(page.getByTestId('chat-incognito-status')).toContainText(
      'Durable memory will not be read or written',
    );
    await expect(page.getByTestId('chat-incognito-toggle')).toHaveAttribute('aria-pressed', 'true');

    await page.getByTestId('chat-input').fill('hello privately');
    await page.getByTestId('chat-send').click();
    await expect.poll(() => submitted?.ephemeralMemory).toBe(true);

    await page.getByTestId('chat-incognito-toggle').click();
    await expect(page.getByTestId('chat-incognito-status')).toBeHidden();
    await expect(page.getByTestId('chat-incognito-toggle')).toHaveAttribute('aria-pressed', 'false');
  });

  test('global search opens a local Wiki page in the side workbench', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL!;
    const token = process.env.RUNTIME_TOKEN!;
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    await page.route(`${baseUrl}/api/wiki/search?*`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          results: [{
            rootId: 'root-ux',
            pageId: 'palette-page',
            title: 'Release brief',
            excerpt: 'Controlled publish recommendation and remaining human decisions.',
            relativePath: 'release-brief.md',
            version: 3,
          }],
        }),
      });
    });
    await page.route(`${baseUrl}/api/wiki/pages/palette-page`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          page: {
            id: 'palette-page',
            rootId: 'root-ux',
            folderId: null,
            title: 'Release brief',
            slug: 'release-brief',
            relativePath: 'release-brief.md',
            version: 3,
            createdAt: '2026-07-25T12:00:00Z',
            updatedAt: '2026-07-25T12:05:00Z',
            excerpt: 'Controlled publish recommendation.',
            wordCount: 12,
            deletedAt: null,
          },
          markdown: '# Release brief\n\nProceed with a controlled publish after the final human checks.',
        }),
      });
    });
    await page.route(`${baseUrl}/api/wiki/pages/palette-page/revisions`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          revisions: [{
            id: 'rev-3',
            pageId: 'palette-page',
            version: 3,
            source: 'ai',
            createdAt: '2026-07-25T12:05:00Z',
            summary: 'Added verified release recommendation',
            markdown: '# Release brief',
          }],
        }),
      });
    });

    await page.goto(`${baseUrl}/`, { waitUntil: 'domcontentloaded' });
    await page.dispatchEvent('body', 'keydown', {
      key: ' ',
      code: 'Space',
      ctrlKey: true,
    });
    const palette = page.getByTestId('command-palette');
    await expect(palette).toBeVisible();
    await palette.getByRole('textbox').fill('# release');
    await expect(palette.getByRole('option', { name: /Release brief/ })).toBeVisible();
    await palette.getByRole('option', { name: /Release brief/ }).click();

    const workbench = page.getByTestId('wiki-workbench');
    await expect(workbench).toBeVisible();
    await expect(workbench).toContainText('Release brief');
    await expect(workbench).toContainText('controlled publish');
    await workbench.getByRole('tab', { name: 'History' }).click();
    await expect(workbench).toContainText('Added verified release recommendation');
    await workbench.getByRole('button', { name: 'Close workbench' }).click();
    await expect(workbench).toBeHidden();
  });

  test('permission pause stays in context and completed work has a receipt', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL!;
    const token = process.env.RUNTIME_TOKEN!;
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    await page.route(`${baseUrl}/api/threads/ux-thread`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          id: 'ux-thread',
          title: 'Release readiness',
          createdAt: '2026-07-25T12:00:00Z',
          updatedAt: '2026-07-25T12:02:00Z',
          messages: [
            {
              id: 'user-ux',
              role: 'user',
              text: 'Check the release evidence.',
              createdAt: '2026-07-25T12:00:00Z',
            },
            {
              id: 'assistant-ux',
              role: 'assistant',
              text: 'The release evidence is internally consistent.',
              createdAt: '2026-07-25T12:02:00Z',
              sources: [{
                url: 'https://example.com/evidence',
                title: 'Release evidence',
                domain: 'example.com',
                excerpt: 'Verified evidence summary.',
              }],
            },
          ],
        }),
      });
    });
    await page.route(`${baseUrl}/api/turns/assistant-ux/trace`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          messageId: 'assistant-ux',
          events: [
            {
              type: 'chat.effect.proposed',
              payload: {
                activityId: 'tool-ux',
                threadId: 'ux-thread',
                messageId: 'assistant-ux',
                tool: 'web_search',
                effect: {
                  kind: 'read',
                  mutating: false,
                  reversible: false,
                  boundary: 'web',
                  summary: 'Web Search',
                  target: 'release evidence',
                  undoStrategy: null,
                  capability: 'WebSearch',
                },
                proposedAt: '2026-07-25T12:01:00Z',
              },
            },
            {
              type: 'chat.tool.started',
              payload: {
                activityId: 'tool-ux',
                threadId: 'ux-thread',
                messageId: 'assistant-ux',
                tool: 'web_search',
                group: 'Web',
                argsPreview: '{"query":"release evidence"}',
                startedAt: '2026-07-25T12:01:00Z',
              },
            },
            {
              type: 'chat.tool.completed',
              payload: {
                activityId: 'tool-ux',
                threadId: 'ux-thread',
                messageId: 'assistant-ux',
                tool: 'web_search',
                ok: true,
                durationMs: 420,
                resultSnippet: '3 current sources',
                error: null,
                completedAt: '2026-07-25T12:01:01Z',
              },
            },
            {
              type: 'chat.effect.completed',
              payload: {
                activityId: 'tool-ux',
                threadId: 'ux-thread',
                messageId: 'assistant-ux',
                tool: 'web_search',
                effect: {
                  kind: 'read',
                  mutating: false,
                  reversible: false,
                  boundary: 'web',
                  summary: 'Web Search',
                  target: 'release evidence',
                  undoStrategy: null,
                  capability: 'WebSearch',
                },
                outcome: {
                  status: 'observed',
                  evidence: 'tool-result',
                  independentlyVerified: false,
                  resolvedTarget: 'release evidence',
                },
                completedAt: '2026-07-25T12:01:01Z',
              },
            },
          ],
        }),
      });
    });
    await page.route(`${baseUrl}/api/permissions/pending`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          requests: [{
            id: 'permission-ux',
            tool: 'file_read',
            group: 'Files',
            argsJson: '{"path":"C:\\\\reports\\\\release.md"}',
            threadId: 'ux-thread',
            turnId: 'assistant-ux',
            createdAt: '2026-07-25T12:03:00Z',
            scope: 'tool',
          }],
        }),
      });
    });
    const decisions: Array<Record<string, unknown>> = [];
    await page.route(`${baseUrl}/api/permissions/respond`, async (route) => {
      decisions.push(route.request().postDataJSON() as Record<string, unknown>);
      await route.fulfill({ contentType: 'application/json', body: '{}' });
    });

    const pageErrors: string[] = [];
    page.on('pageerror', (error) => pageErrors.push(error.message));
    const navigation = await page.goto(`${baseUrl}/chat/ux-thread`, { waitUntil: 'domcontentloaded' });
    expect(navigation?.status()).toBe(200);
    await page.waitForTimeout(250);
    expect(pageErrors).toEqual([]);
    await expect(page.getByTestId('route-chat-thread')).toBeVisible();

    const messageList = page.getByTestId('chat-message-list');
    const permission = messageList.getByTestId('permission-modal');
    await expect(permission).toBeVisible();
    await expect(permission).toContainText('Read a local file');
    await expect(page.getByTestId('chat-input')).toBeDisabled();
    await expect(page.getByTestId('permission-once')).toBeFocused();

    const receipt = page.getByTestId('work-receipt-assistant-ux');
    await expect(receipt).toBeVisible();
    await expect(receipt).toContainText('Web Search');
    await receipt.getByRole('button', { name: /Work completed/ }).click();
    await expect(receipt).toContainText('3 current sources');
    await expect(receipt).toContainText('read only');
    await expect(receipt).toContainText('Tool result only');
    await expect(receipt).toContainText('Source-backed evidence · 80%');
    await receipt.getByRole('button', { name: 'Mark outcome accurate' }).click();
    await expect(receipt.getByRole('button', { name: 'Mark outcome accurate' })).toHaveAttribute('aria-pressed', 'true');
    await expect.poll(async () => {
      const response = await context.request.get(`${baseUrl}/api/insights`, {
        headers: { Authorization: `Bearer ${token}` },
      });
      const body = await response.json() as {
        metrics: Array<{ key: string; status: string }>;
      };
      return body.metrics.find((metric) => metric.key === 'trust-calibration')?.status;
    }).toBe('measured');

    await page.getByTestId('permission-once').click();
    await expect(permission).toBeHidden();
    await expect.poll(() => decisions.length).toBe(1);
    expect(decisions[0]).toEqual({
      id: 'permission-ux',
      decision: 'once',
      scope: 'tool',
    });
  });

  test('a versioned Wiki effect can be undone and redone from its receipt', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL!;
    const token = process.env.RUNTIME_TOKEN!;
    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });
    const headers = { Authorization: `Bearer ${token}` };

    const rootResponse = await context.request.post(`${baseUrl}/api/wiki/roots`, {
      headers,
      data: { name: `Receipt undo ${Date.now()}` },
    });
    expect(rootResponse.ok()).toBeTruthy();
    const root = await rootResponse.json() as { id: string };
    const createdResponse = await context.request.post(
      `${baseUrl}/api/wiki/roots/${encodeURIComponent(root.id)}/pages`,
      {
        headers,
        data: { title: 'Undo proof', markdown: '# Undo proof\n\nOriginal state.' },
      },
    );
    const created = await createdResponse.json() as { page: { id: string; version: number } };
    const updatedResponse = await context.request.patch(
      `${baseUrl}/api/wiki/pages/${encodeURIComponent(created.page.id)}`,
      {
        headers,
        data: {
          markdown: '# Undo proof\n\nAssistant-applied state.',
          expectedVersion: created.page.version,
          source: 'ai',
          summary: 'Assistant effect under test',
        },
      },
    );
    expect(updatedResponse.ok()).toBeTruthy();
    const updated = await updatedResponse.json() as { page: { id: string; version: number } };

    await page.route(`${baseUrl}/api/threads/effect-thread`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          id: 'effect-thread',
          title: 'Effect receipt',
          createdAt: '2026-07-25T12:00:00Z',
          updatedAt: '2026-07-25T12:02:00Z',
          messages: [
            {
              id: 'effect-user',
              role: 'user',
              text: 'Update my Wiki page.',
              createdAt: '2026-07-25T12:00:00Z',
            },
            {
              id: 'effect-assistant',
              role: 'assistant',
              text: 'Updated the Wiki page.',
              createdAt: '2026-07-25T12:02:00Z',
            },
          ],
        }),
      });
    });
    const effect = {
      kind: 'update',
      mutating: true,
      reversible: true,
      boundary: 'local',
      summary: 'Wiki Page Update',
      target: created.page.id,
      undoStrategy: 'wiki-revision',
      capability: 'WikiWrite',
    };
    await page.route(`${baseUrl}/api/turns/effect-assistant/trace`, async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          messageId: 'effect-assistant',
          events: [
            {
              type: 'chat.effect.proposed',
              payload: {
                activityId: 'effect-tool',
                threadId: 'effect-thread',
                messageId: 'effect-assistant',
                tool: 'wiki_page_update',
                effect,
                proposedAt: '2026-07-25T12:01:00Z',
              },
            },
            {
              type: 'chat.tool.started',
              payload: {
                activityId: 'effect-tool',
                threadId: 'effect-thread',
                messageId: 'effect-assistant',
                tool: 'wiki_page_update',
                group: 'Files',
                argsPreview: JSON.stringify({ page_id: created.page.id }),
                startedAt: '2026-07-25T12:01:00Z',
              },
            },
            {
              type: 'chat.tool.completed',
              payload: {
                activityId: 'effect-tool',
                threadId: 'effect-thread',
                messageId: 'effect-assistant',
                tool: 'wiki_page_update',
                ok: true,
                durationMs: 18,
                resultSnippet: JSON.stringify({ document: { page: updated.page } }),
                error: null,
                completedAt: '2026-07-25T12:01:01Z',
              },
            },
            {
              type: 'chat.effect.completed',
              payload: {
                activityId: 'effect-tool',
                threadId: 'effect-thread',
                messageId: 'effect-assistant',
                tool: 'wiki_page_update',
                effect,
                outcome: {
                  status: 'applied',
                  evidence: 'versioned-wiki-state',
                  independentlyVerified: true,
                  resolvedTarget: created.page.id,
                },
                completedAt: '2026-07-25T12:01:01Z',
              },
            },
          ],
        }),
      });
    });
    await page.route(`${baseUrl}/api/permissions/pending`, async (route) => {
      await route.fulfill({ contentType: 'application/json', body: '{"requests":[]}' });
    });

    await page.goto(`${baseUrl}/chat/effect-thread`, { waitUntil: 'domcontentloaded' });
    const receipt = page.getByTestId('work-receipt-effect-assistant');
    await receipt.getByRole('button', { name: /Work completed/ }).click();
    await expect(receipt).toContainText('update effect');
    await expect(receipt).toContainText('Verified state');

    await receipt.getByRole('button', { name: 'Undo Wiki effect' }).click();
    await expect(receipt).toContainText('Wiki effect undone');
    const undoneResponse = await context.request.get(
      `${baseUrl}/api/wiki/pages/${encodeURIComponent(created.page.id)}`,
      { headers },
    );
    const undone = await undoneResponse.json() as { markdown: string };
    expect(undone.markdown).toContain('Original state.');

    await receipt.getByRole('button', { name: 'Redo Wiki effect' }).click();
    await expect(receipt.getByRole('button', { name: 'Undo Wiki effect' })).toBeVisible();
    const redoneResponse = await context.request.get(
      `${baseUrl}/api/wiki/pages/${encodeURIComponent(created.page.id)}`,
      { headers },
    );
    const redone = await redoneResponse.json() as { markdown: string };
    expect(redone.markdown).toContain('Assistant-applied state.');
  });
});
