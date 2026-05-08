import { createFileRoute, redirect } from '@tanstack/react-router';

/**
 * Legacy URL shape: settings used to support per-category routes (/settings/audio,
 * /settings/files, etc.) but the working surface is the tabbed /settings page.
 * Redirect any deep-link to the canonical route so old bookmarks still land
 * somewhere useful. Using a loader-level redirect runs before render, so the
 * stub never paints.
 */
export const Route = createFileRoute('/settings/$category')({
  beforeLoad: () => {
    throw redirect({ to: '/settings', replace: true });
  },
});
