import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/memory')({
  component: () => (
    <PageScaffold
      testId="route-memory"
      title="Memory"
      subtitle="Saved facts, preferences, and context. You stay in control."
    />
  ),
});
