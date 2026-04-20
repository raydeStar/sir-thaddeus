import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/chat')({
  component: () => (
    <PageScaffold
      testId="route-chat"
      title="Chat"
      subtitle="Open a thread to continue a conversation, or start a new one."
    />
  ),
});
