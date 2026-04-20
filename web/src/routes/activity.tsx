import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/activity')({
  component: () => (
    <PageScaffold
      testId="route-activity"
      title="Activity"
      subtitle="Tool calls, permission decisions, and audit entries."
    />
  ),
});
