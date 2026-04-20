import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/activity/$entryId')({
  component: ActivityEntryRoute,
});

function ActivityEntryRoute() {
  const { entryId } = Route.useParams();
  return (
    <PageScaffold
      testId="route-activity-entry"
      title={`Activity entry ${entryId}`}
      subtitle="Inputs, outputs, and audit metadata for this tool call."
    />
  );
}
