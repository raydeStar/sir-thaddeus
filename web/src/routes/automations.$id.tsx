import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/automations/$id')({
  component: AutomationRoute,
});

function AutomationRoute() {
  const { id } = Route.useParams();
  return (
    <PageScaffold
      testId="route-automation-detail"
      title={`Automation ${id}`}
      subtitle="Trigger, action, schedule, and recent runs."
    />
  );
}
