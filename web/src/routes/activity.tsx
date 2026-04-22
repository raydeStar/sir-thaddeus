import { createFileRoute, Outlet } from '@tanstack/react-router';

/**
 * Pathless layout for the /activity route family. Children are activity.index
 * (list) and activity.$entryId (single entry). The layout renders only the
 * <Outlet />; each child supplies its own page chrome.
 */
export const Route = createFileRoute('/activity')({
  component: ActivityLayout,
});

function ActivityLayout() {
  return <Outlet />;
}
