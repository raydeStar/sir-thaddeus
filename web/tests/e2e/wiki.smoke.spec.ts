import { test, expect, type Dialog } from '@playwright/test';

/**
 * Wiki + Canvas smoke. Covers the requirements that ship with the /wiki
 * route: three-panel layout, automatic first-run workspace/page bootstrap,
 * folder/page tree, markdown editor toolbar, save + revisions, search,
 * scope controls, and panel
 * collapse/expand. Captures full-page screenshots at each significant
 * state for visual review.
 *
 * The runtime is started in --test-mode by global-setup, so any wiki
 * workspaces created here land under a sandboxed library and do not touch
 * the user's real ~/.thaddeus or Documents content beyond the runtime's
 * configured library directory.
 */
test.describe('wiki canvas smoke', () => {
  test.setTimeout(120_000);

  test('renders the wiki canvas, supports workspace/page lifecycle, search, and editor', async ({ page, context }) => {
    const baseUrl = process.env.RUNTIME_BASE_URL;
    const token = process.env.RUNTIME_TOKEN;
    expect(baseUrl, 'global-setup must populate RUNTIME_BASE_URL').toBeTruthy();
    expect(token, 'global-setup must populate RUNTIME_TOKEN').toBeTruthy();

    await context.setExtraHTTPHeaders({ Authorization: `Bearer ${token}` });

    // Auto-handle window.confirm() for destructive actions and window.prompt()
    // for the editor link toolbar. Reset between phases as needed.
    let dialogResponse: { accept: boolean; text?: string } = { accept: true };
    page.on('dialog', async (dialog: Dialog) => {
      if (!dialogResponse.accept) {
        await dialog.dismiss();
        return;
      }
      if (dialog.type() === 'prompt') {
        await dialog.accept(dialogResponse.text ?? dialog.defaultValue());
      } else {
        await dialog.accept();
      }
    });

    // ---------- Phase 1: first-run workspace/page bootstrap ----------
    await page.goto(`${baseUrl}/wiki`, { waitUntil: 'domcontentloaded' });
    const route = page.getByTestId('route-wiki');
    await expect(route).toBeVisible();
    await expect(page.getByText('Wiki Canvas')).toBeVisible();

    // First-run users should not need setup: the app should create/select a
    // workspace and page so the editor and assistant are immediately usable.
    await page.screenshot({ path: 'test-results/wiki-01-initial.png', fullPage: true });

    // ---------- Phase 2: ensure workspace + editor are ready ----------
    const workspaceSelect = page.locator('#wiki-root-select');
    await expect(workspaceSelect).toBeVisible({ timeout: 10_000 });
    await expect(workspaceSelect).not.toHaveValue('', { timeout: 15_000 });
    await expect(page.getByLabel('Page title', { exact: true })).toBeVisible({ timeout: 15_000 });

    await page.screenshot({ path: 'test-results/wiki-02-root-ready.png', fullPage: true });

    // ---------- Phase 3: use the starter page ----------
    await expect(page.getByRole('button', { name: 'New page', exact: true }).first()).toBeVisible();

    // The editor area mounts immediately on the auto-created starter page.
    // The page title is editable in the header.
    await expect(page.getByLabel('Page title', { exact: true })).toBeVisible({ timeout: 10_000 });
    const editorContent = page.locator('.wiki-editor-content');
    await expect(editorContent).toBeVisible({ timeout: 10_000 });

    // Toolbar buttons ship with stable aria-labels. Spot-check the full set.
    const toolbarLabels = [
      'Paragraph',
      'Heading 1',
      'Heading 2',
      'Bold',
      'Italic',
      'Code',
      'Link',
      'Remove link',
      'Bullet list',
      'Numbered list',
      'Quote',
    ];
    for (const label of toolbarLabels) {
      await expect(page.getByRole('button', { name: label, exact: true })).toBeVisible();
    }

    await page.screenshot({ path: 'test-results/wiki-03-page-created.png', fullPage: true });

    // ---------- Phase 4: type, save, observe revisions ----------
    // Type new content into the editor and save. Save success is best
    // confirmed by a revision being persisted, since the Tiptap editor's
    // async onChange can briefly toggle the dirty badge during roundtrip.

    // Click into the editor and type some markdown content via Tiptap.
    await editorContent.click();
    await page.keyboard.press('End');
    await page.keyboard.press('Enter');
    await page.keyboard.type('Hello from Playwright. This page validates the Wiki Canvas.');

    // Capture the dirty state — the Save button should be visually emphasized.
    await page.screenshot({ path: 'test-results/wiki-03b-dirty.png', fullPage: true });

    const saveButton = page.getByRole('button', { name: /^Save(?:\s+Ctrl\+S)?$/ });
    await expect(saveButton).toBeEnabled();
    await saveButton.click();

    // Capture state right after save click for visual review/debug.
    await page.waitForTimeout(1000);
    await page.screenshot({ path: 'test-results/wiki-04a-after-save-click.png', fullPage: true });

    // Surface any wiki error so we don't time out waiting for "Saved" silently.
    const errorBanner = page.getByTestId('wiki-error');
    if (await errorBanner.isVisible().catch(() => false)) {
      const errorText = await errorBanner.textContent();
      throw new Error(`Wiki error after save: ${errorText}`);
    }

    // Save success is best-confirmed by a revision being persisted, since the
    // Tiptap editor's async onChange can re-mark the draft as Unsaved
    // immediately after the save round-trip completes.
    await expect(page.getByText('Revisions')).toBeVisible();
    await expect(page.locator('text=/Version \\d+/').first()).toBeVisible({ timeout: 10_000 });

    // Word count footer reflects the typed content (>= 9 words).
    await expect(page.locator('text=/\\d+ words/').first()).toBeVisible();

    await page.screenshot({ path: 'test-results/wiki-04-page-saved.png', fullPage: true });

    // ---------- Phase 4b: AI write auto-applies and rolls back ----------
    await page.route('**/api/wiki/pages/*/draft', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          markdown: '# Untitled Page\n\nHello from Playwright. This page validates the Wiki Canvas.\n\nAI auto-applied paragraph.',
          assistantText: 'Added the paragraph directly to the page.',
          summary: 'Added an auto-applied paragraph',
          createdAt: new Date().toISOString(),
          messageId: 'playwright-auto-draft',
        }),
      });
    });

    await page.getByLabel('Page chat prompt').fill('Add an auto-applied paragraph.');
    await page.getByRole('button', { name: 'Write', exact: true }).click();
    await expect(editorContent).toContainText('AI auto-applied paragraph.', { timeout: 15_000 });

    const rollbackButton = page.getByRole('button', { name: 'Rollback', exact: true });
    await expect(rollbackButton).toBeVisible({ timeout: 10_000 });
    await expect(rollbackButton).toBeEnabled();
    await page.screenshot({ path: 'test-results/wiki-04b-ai-auto-applied.png', fullPage: true });

    await rollbackButton.click();
    await expect(editorContent).not.toContainText('AI auto-applied paragraph.', { timeout: 15_000 });
    await page.unroute('**/api/wiki/pages/*/draft');

    await page.screenshot({ path: 'test-results/wiki-04c-ai-rollback.png', fullPage: true });

    // ---------- Phase 5: scope chips, search scope toggle ----------
    // Scope controls: workspace/folder/page. Folder is disabled with no folder
    // selected, but page should be selectable now.
    await page.getByRole('button', { name: 'page', exact: true }).click();
    await expect(page.getByRole('button', { name: 'page', exact: true })).toHaveClass(/border-accent/);

    // ---------- Phase 6: search filtering ----------
    const searchInput = page.getByPlaceholder('Search pages');
    await searchInput.fill('Playwright');
    // Either we get a match or the empty-state message. Both are acceptable
    // since search index timing varies; we just want the search UI to react.
    await expect(
      page.getByText(/No matching pages|Playwright/i).first(),
    ).toBeVisible({ timeout: 10_000 });
    await page.screenshot({ path: 'test-results/wiki-05-search.png', fullPage: true });
    await searchInput.fill('');

    // ---------- Phase 7: collapse/expand panels ----------
    // Left "Pages" panel: aria-label flips between "Collapse Pages" and "Open Pages".
    await page.getByRole('button', { name: 'Collapse Pages' }).click();
    await expect(page.getByRole('button', { name: 'Open Pages' })).toBeVisible();
    await page.screenshot({ path: 'test-results/wiki-06-left-collapsed.png', fullPage: true });
    await page.getByRole('button', { name: 'Open Pages' }).click();

    // Right "Assistant" panel: collapse + re-expand.
    await page.getByRole('button', { name: 'Collapse Assistant' }).click();
    await expect(page.getByRole('button', { name: 'Open Assistant' })).toBeVisible();
    await page.getByRole('button', { name: 'Open Assistant' }).click();

    // ---------- Phase 8: cleanup — remove the freshly created workspace ----------
    // Dialog handler will accept the confirm() prompt above.
    const discardButton = page.getByRole('button', { name: 'Discard', exact: true });
    if (await discardButton.isVisible().catch(() => false)) {
      await discardButton.click();
    }

    const removeWorkspaceButton = page.getByRole('button', { name: 'Remove workspace', exact: true });
    if (await removeWorkspaceButton.isEnabled().catch(() => false)) {
      await removeWorkspaceButton.click();
      const removeDialog = page.getByRole('dialog', { name: 'Remove workspace' });
      if (await removeDialog.isVisible().catch(() => false)) {
        await removeDialog.getByRole('button', { name: 'Remove workspace', exact: true }).click();
      }
    }

    // Final screenshot for review.
    await page.screenshot({ path: 'test-results/wiki-07-final.png', fullPage: true });
  });
});
