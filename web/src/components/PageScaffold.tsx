import { ReactNode } from 'react';

interface PageScaffoldProps {
  testId: string;
  title: string;
  subtitle?: string;
  children?: ReactNode;
}

/**
 * Phase-1 placeholder layout for every route. Real screens land in Phases 2–8 per
 * spec §23 build order. The `data-testid` attributes are stable so Playwright can
 * route between screens once we wire up navigation tests.
 */
export function PageScaffold({ testId, title, subtitle, children }: PageScaffoldProps) {
  return (
    <section data-testid={testId} className="mx-auto max-w-3xl px-6 py-10">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold text-thaddeus-ink">{title}</h1>
        {subtitle ? <p className="mt-1 text-sm text-slate-600">{subtitle}</p> : null}
      </header>
      <div className="rounded-lg border border-slate-200 bg-white p-6 shadow-sm">
        {children ?? <p className="text-sm text-slate-500">Coming in a later phase.</p>}
      </div>
    </section>
  );
}
