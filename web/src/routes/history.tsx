import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/history')({
  component: () => (
    <PageScaffold
      testId="route-history"
      title="History"
      subtitle="Past chats, grouped by day."
    />
  ),
});
