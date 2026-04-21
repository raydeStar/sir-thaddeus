import { ReactNode } from 'react';

interface PageScaffoldProps {
  testId: string;
  title: string;
  subtitle?: string;
  children?: ReactNode;
  /**
   * When true, renders the body without the framed surface so screens (like the
   * chat thread) can take full advantage of the column width.
   */
  bare?: boolean;
}

export function PageScaffold({ testId, title, subtitle, children, bare }: PageScaffoldProps) {
  return (
    <section data-testid={testId} className="mx-auto w-full max-w-3xl px-6 py-10 md:px-8 md:py-14">
      <header className="mb-8">
        <h1 className="text-3xl font-semibold tracking-tightest text-ink">{title}</h1>
        {subtitle ? <p className="mt-2 text-sm text-ink-muted">{subtitle}</p> : null}
      </header>
      {bare ? (
        <div>{children}</div>
      ) : (
        <div className="surface p-6 md:p-8">
          {children ?? <p className="text-sm text-ink-muted">Coming in a later phase.</p>}
        </div>
      )}
    </section>
  );
}
