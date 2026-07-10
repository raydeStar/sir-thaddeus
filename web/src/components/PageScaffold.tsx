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
      className={`mx-auto w-full ${widthClass[width]} px-5 py-9 sm:px-6 md:px-10 md:py-14`}
    >
      {!bare ? (
        <header className="mb-9 border-b border-line pb-7">
          <h1 className="text-[2rem] font-semibold leading-[1.1] tracking-[-0.035em] text-ink md:text-[2.25rem]">{title}</h1>
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
