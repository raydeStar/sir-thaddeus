import { test, expect, type Dialog, type Route } from '@playwright/test';

/**
 * Wiki AI assistant end-to-end walkthrough — covers the user-requested flow:
 *   1. Create a new page.
 *   2. Have the assistant write a section about a cat named Clarence.
 *   3. Have the assistant do a full rewrite.
 *   4. Select one paragraph and ask for a paragraph-only rewrite.
 *   5. Verify the revisions panel and the Rollback control.
 *
 * The runtime is launched in --test-mode by global-setup so the work stays
 * inside the sandbox library. The two AI endpoints (/draft and
 * /selection/rewrite) are stubbed so the test is deterministic and does not
 * depend on a live LLM. The runtime still persists every save through its
 * normal /api/wiki/pages/{id} update path, which exercises the auto-apply
 * pipeline (draftPage → updateWikiPage(source: 'ai')).
 */
test.describe('wiki assistant flow', () => {
  test.setTimeout(180_000);

  test('write, full rewrite, paragraph rewrite, revisions, rollback', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl, 'global-setup must populate RUNTIME_BASE_URL').toBeTruthy();
    expect(token, 'global-setup must populate RUNTIME_TOKEN').toBeTruthy();

    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    page.on('dialog', async (dialog: Dialog) => {
      if (dialog.type() === 'prompt') await dialog.accept(dialog.defaultValue());
      else await dialog.accept();
    });

    // ---------- Bootstrap: open /wiki and wait for the starter page. ----------
    await page.goto(`${baseUrl}/wiki`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('route-wiki')).toBeVisible();

    const workspaceSelect = page.locator('#wiki-root-select');
    await expect(workspaceSelect).toBeVisible({ timeout: 15_000 });
    await expect(workspaceSelect).not.toHaveValue('', { timeout: 20_000 });

    const titleInput = page.getByLabel('Page title', { exact: true });
    await expect(titleInput).toBeVisible({ timeout: 15_000 });

    const editorContent = page.locator('.wiki-editor-content');
    await expect(editorContent).toBeVisible({ timeout: 15_000 });

    // ---------- Step 1: create a new page. ----------
    // The header "New page" button is the most stable target.
    const newPageButton = page.getByRole('button', { name: 'New page', exact: true }).first();
    await newPageButton.click();

    // After creation the editor is mounted on the newly created page; its
    // title becomes editable. Wait until the version-keyed editor remounts.
    await expect(titleInput).toBeVisible({ timeout: 10_000 });
    await expect(editorContent).toBeVisible({ timeout: 10_000 });
    await page.screenshot({ path: 'test-results/wiki-assistant-01-new-page.png', fullPage: true });

    // ---------- Step 2: stub /draft to return the Clarence section, click Write. ----------
    const clarenceMarkdown =
      '# Clarence the Cat\n\n' +
      'Clarence is a tabby with a slow, deliberate stride and a tail that always finds the highest shelf.\n\n' +
      'He spends his mornings on the windowsill watching birds he has no intention of catching.\n\n' +
      'By afternoon he supervises the household from a sunbeam in the hallway.';

    let draftReply = clarenceMarkdown;
    let draftSummary = 'Wrote a section about Clarence the cat';
    const stubSources = [
      {
        pageId: 'playwright-source-page',
        title: 'Cat Reference Notes',
        relativePath: 'cat-reference-notes.md',
        snippet: 'Clarence reference material used by the assistant.',
        score: 12.5,
      },
    ];
    await page.route('**/api/wiki/pages/*/draft', async (route: Route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          markdown: draftReply,
          assistantText: 'Drafted the requested section.',
          summary: draftSummary,
          createdAt: new Date().toISOString(),
          messageId: `playwright-draft-${Date.now()}`,
          sources: stubSources,
        }),
      });
    });

    const promptInput = page.getByLabel('Page chat prompt');
    await promptInput.fill('Write a section about a cat named Clarence.');
    await page.getByRole('button', { name: 'Write', exact: true }).click();

    await expect(editorContent).toContainText('Clarence', { timeout: 20_000 });
    await expect(editorContent).toContainText('windowsill', { timeout: 20_000 });
    await expect(page.getByTestId('wiki-assistant-sources').getByText('Cat Reference Notes')).toBeVisible({ timeout: 10_000 });
    await page.screenshot({ path: 'test-results/wiki-assistant-02-clarence.png', fullPage: true });

    // ---------- Step 3: full rewrite via Write again with new content. ----------
    const rewrittenMarkdown =
      '# Clarence the Cat — Field Notes\n\n' +
      'Clarence has a precise routine, and any deviation is treated as a personal slight.\n\n' +
      'He greets visitors with a long, evaluative blink before deciding whether they are worthy of a sniff.\n\n' +
      'Dinner is served at six, no exceptions, and he will sit by the bowl until the household complies.';
    draftReply = rewrittenMarkdown;
    draftSummary = 'Full rewrite of the Clarence page';

    await promptInput.fill('Rewrite the entire page in a more formal field-notes voice.');
    await page.getByRole('button', { name: 'Write', exact: true }).click();

    await expect(editorContent).toContainText('Field Notes', { timeout: 20_000 });
    await expect(editorContent).toContainText('precise routine', { timeout: 20_000 });
    await expect(editorContent).not.toContainText('windowsill');
    await page.screenshot({ path: 'test-results/wiki-assistant-03-full-rewrite.png', fullPage: true });

    // ---------- Step 4: select one paragraph and ask for a rewrite. ----------
    // Stub /selection/rewrite. The store calls it with the markdown the
    // editor sent up; we don't care about that text — we just need to
    // return a deterministic page-level markdown that swaps the chosen
    // paragraph. Use the precise-routine paragraph as the target.
    const targetParagraph =
      'Clarence has a precise routine, and any deviation is treated as a personal slight.';
    const replacementParagraph =
      'Clarence keeps a tight schedule, and even a five-minute slip earns a stern, judgmental stare.';
    const selectionRewrittenMarkdown = rewrittenMarkdown.replace(targetParagraph, replacementParagraph);

    await page.route('**/api/wiki/pages/*/selection/rewrite', async (route: Route) => {
      const request = route.request();
      const body = request.postDataJSON() as { selectedText?: string } | null;
      // Sanity check: the editor must send non-empty selection text. If this
      // ever regresses to an empty payload the test fails fast and loudly.
      expect(body?.selectedText && body.selectedText.length > 0, 'selectedText must be sent').toBeTruthy();
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          selectedText: body?.selectedText ?? targetParagraph,
          replacementText: replacementParagraph,
          markdown: selectionRewrittenMarkdown,
          assistantText: 'Rewrote just the selected paragraph.',
          summary: 'Rewrote the selected paragraph',
          createdAt: new Date().toISOString(),
          messageId: `playwright-selection-${Date.now()}`,
          sources: stubSources,
        }),
      });
    });

    // Fill the rewrite prompt first — the button is gated on both a
    // non-empty prompt and a non-empty selection. Clicking into the editor
    // afterwards moves focus but leaves the prompt's controlled value intact.
    await promptInput.fill('Make the tone slightly drier.');

    // Drive the selection through real keyboard input. ProseMirror's view
    // ignores synthetic DOM selectionchange events, so we click into the
    // target paragraph (placing the caret) and then issue Home + Shift+End
    // to select the whole line through the standard editing pipeline.
    const targetLocator = editorContent.getByText(targetParagraph, { exact: false });
    await expect(targetLocator).toBeVisible();
    await targetLocator.click(); // places caret in the paragraph
    await page.keyboard.press('Home');
    await page.keyboard.down('Shift');
    await page.keyboard.press('End');
    await page.keyboard.up('Shift');

    // Confirm a non-empty selection actually reached the store before we
    // dispatch the rewrite.
    const rewriteButton = page.getByRole('button', { name: 'Rewrite selection', exact: true });
    await expect(rewriteButton).toBeEnabled({ timeout: 10_000 });

    await rewriteButton.click();

    await expect(editorContent).toContainText('tight schedule', { timeout: 20_000 });
    await expect(editorContent).toContainText('judgmental stare', { timeout: 20_000 });
    await expect(editorContent).not.toContainText('precise routine');
    // Other paragraphs from the previous full rewrite must remain untouched.
    await expect(editorContent).toContainText('Dinner is served at six');
    await page.screenshot({ path: 'test-results/wiki-assistant-04-selection-rewrite.png', fullPage: true });

    // ---------- Step 5: revisions list and Rollback. ----------
    await expect(page.getByText('Revisions')).toBeVisible();
    // After three AI saves we expect at least three Version entries.
    const versionEntries = page.locator('text=/Version \\d+/');
    await expect.poll(async () => versionEntries.count(), { timeout: 15_000 }).toBeGreaterThanOrEqual(3);

    const headerRollback = page.getByRole('button', { name: 'Rollback', exact: true });
    await expect(headerRollback).toBeVisible({ timeout: 10_000 });
    await expect(headerRollback).toBeEnabled();
    await headerRollback.click();

    // Rolling back the most recent AI save (the selection rewrite) restores
    // the prior full-rewrite content.
    await expect(editorContent).not.toContainText('tight schedule', { timeout: 20_000 });
    await expect(editorContent).toContainText('precise routine', { timeout: 20_000 });
    await page.screenshot({ path: 'test-results/wiki-assistant-05-rollback.png', fullPage: true });

    await page.unroute('**/api/wiki/pages/*/draft');
    await page.unroute('**/api/wiki/pages/*/selection/rewrite');
  });
});
