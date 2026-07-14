import type { ReactNode } from 'react';

interface SettingsSectionProps {
  title: string;
  description?: string;
  children: ReactNode;
}

/** Consistent titled section used by settings panels. */
export function SettingsSection({ title, description, children }: SettingsSectionProps) {
  return (
    <section className="space-y-5 pb-10 border-b border-line last:border-0 last:pb-0">
      <header>
        <h2 className="text-[15px] font-semibold tracking-tight text-ink">{title}</h2>
        {description ? (
          <p className="mt-1 text-[13px] text-ink-muted">{description}</p>
        ) : null}
      </header>
      <div className="space-y-5">{children}</div>
    </section>
  );
}
