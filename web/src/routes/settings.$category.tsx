import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/settings/$category')({
  component: SettingsCategoryRoute,
});

function SettingsCategoryRoute() {
  const { category } = Route.useParams();
  return (
    <PageScaffold
      testId="route-settings-category"
      title={`Settings · ${category}`}
      subtitle="Settings for this category will appear here."
    />
  );
}
