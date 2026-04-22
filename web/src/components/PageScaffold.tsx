import { ReactNode } from 'react';

interface PageScaffoldProps {
  testId: string;
  title: string;
  subtitle?: string;
  children?: ReactNode;
  /**
   * When true, the header is omitted. Use for pages that need edge-to-edge
   * layout (Home hero, compact panel, etc.).
   */
  bare?: boolean;
  /**
   * When set, the body content can grow to a wider reading column than the
   * default (e.g. Settings tabs). Default is max-w-3xl for comfortable reading.
   */
  width?: 'narrow' | 'default' | 'wide';
}

const widthClass = {
  narrow: 'max-w-2xl',
  default: 'max-w-3xl',
  wide: 'max-w-5xl',
} as const;

export function PageScaffold({
  testId,
  title,
  subtitle,
  children,
  bare,
  width = 'default',
}: PageScaffoldProps) {
  return (
    <section
      data-testid={testId}
      className={`mx-auto w-full ${widthClass[width]} px-6 py-12 md:px-10 md:py-16`}
    >
      {!bare ? (
        <header className="mb-10">
          <h1 className="text-[2.25rem] font-semibold leading-[1.1] text-ink">{title}</h1>
          {subtitle ? (
            <p className="mt-2 text-[15px] text-ink-muted">{subtitle}</p>
          ) : null}
        </header>
      ) : null}
      <div>
        {children ?? (
          <p className="text-sm text-ink-muted">Coming in a later phase.</p>
        )}
      </div>
    </section>
  );
}
