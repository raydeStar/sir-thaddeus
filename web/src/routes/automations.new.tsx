import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/automations/new')({
  component: () => (
    <PageScaffold
      testId="route-automation-new"
      title="New automation"
      subtitle="Describe a routine task. We'll suggest the rest."
    />
  ),
});
