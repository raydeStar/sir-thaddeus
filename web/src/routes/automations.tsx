import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/automations')({
  component: () => (
    <PageScaffold
      testId="route-automations"
      title="Automations"
      subtitle="Saved instructions Sir Thaddeus runs on a schedule."
    />
  ),
});
