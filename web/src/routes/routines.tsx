import { createFileRoute, Outlet } from '@tanstack/react-router';

export const Route = createFileRoute('/routines')({
  component: () => <Outlet />,
});
