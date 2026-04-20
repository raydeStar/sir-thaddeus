import { createFileRoute, Outlet } from '@tanstack/react-router';

/**
 * Pathless layout for the /chat route family. Children are chat.index (list)
 * and chat.$threadId (single conversation). The layout renders only the
 * <Outlet />; each child supplies its own page chrome.
 */
export const Route = createFileRoute('/chat')({
  component: ChatLayout,
});

function ChatLayout() {
  return <Outlet />;
}
