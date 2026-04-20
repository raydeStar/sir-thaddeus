import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/diagnostics')({
  component: () => (
    <PageScaffold
      testId="route-diagnostics"
      title="Diagnostics"
      subtitle="Local logs, runtime status, and a one-click bug report."
    />
  ),
});
