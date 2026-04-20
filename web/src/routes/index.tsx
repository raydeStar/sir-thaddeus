import { createFileRoute } from '@tanstack/react-router';
import { PageScaffold } from '../components/PageScaffold';

export const Route = createFileRoute('/')({
  component: HomeRoute,
});

function HomeRoute() {
  return (
    <PageScaffold
      testId="route-home"
      title="Welcome to Sir Thaddeus"
      subtitle="Phase 1 scaffold. The workspace is wired to the runtime over the loopback bridge."
    >
      <ul className="list-disc space-y-1 pl-6 text-sm text-slate-600">
        <li>The badge in the top-right reflects the runtime&rsquo;s authoritative state.</li>
        <li>Navigation slots for Chat, History, Activity, Memory, Automations, Settings, and Diagnostics are stubbed.</li>
        <li>Real screens land in Phases 2&ndash;8 per the build order.</li>
      </ul>
    </PageScaffold>
  );
}
