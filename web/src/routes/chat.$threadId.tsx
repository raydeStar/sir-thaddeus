import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/chat/$threadId')({
  component: ChatThreadRoute,
});

function ChatThreadRoute() {
  const { threadId } = Route.useParams();
  return (
    <PageScaffold
      testId="route-chat-thread"
      title={`Thread ${threadId}`}
      subtitle="Conversation timeline and tool activity will render here."
    />
  );
}
