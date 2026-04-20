import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/onboarding')({
  component: () => (
    <PageScaffold
      testId="route-onboarding"
      title="Onboarding"
      subtitle="First-run guidance, consent, and audio setup."
    />
  ),
});
