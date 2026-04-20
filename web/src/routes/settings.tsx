import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/settings')({
  component: () => (
    <PageScaffold
      testId="route-settings"
      title="Settings"
      subtitle="Voice, models, network, security, and theme."
    />
  ),
});
