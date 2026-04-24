import { useState } from 'react';
import type { ChatMessageSource } from '@thaddeus/shared-types';
import { ArrowUpRight, Clock3, Globe, Sparkles } from 'lucide-react';

interface SourceCardsProps {
  sources: readonly ChatMessageSource[];
}

export function SourceCards({ sources }: SourceCardsProps) {
  if (sources.length === 0) return null;

  return (
    <section className="mt-6 space-y-3" data-testid="chat-source-cards">
      <div className="flex items-center gap-3">
        <div className="inline-flex items-center gap-2 rounded-full border border-line bg-canvas-raised px-3 py-1 text-[11px] font-medium uppercase tracking-[0.12em] text-accent shadow-[0_8px_24px_rgba(28,27,25,0.04)] dark:shadow-[0_8px_24px_rgba(0,0,0,0.28)]">
          <Sparkles className="h-3.5 w-3.5" strokeWidth={2} />
          <span>Latest reporting</span>
        </div>
        <div className="h-px flex-1 bg-gradient-to-r from-line-strong via-line to-transparent" />
        <span className="text-[11px] uppercase tracking-[0.08em] text-ink-subtle">
          {sources.length} {sources.length === 1 ? 'source' : 'sources'}
        </span>
      </div>

      <div className="grid gap-4 md:grid-cols-2 md:items-stretch">
      {sources.map((source, index) => {
        const title = source.title?.trim() || fallbackTitle(source.url, source.domain);
        const domain = source.domain?.trim() || fallbackDomain(source.url);
        const excerpt = truncate(source.excerpt?.trim() || '', index === 0 ? 240 : 110);
        const publishedLabel = formatPublishedAt(source.publishedAt);
        const isFeatured = index === 0 && (sources.length === 1 || sources.length >= 3);
        const eyebrow = isFeatured ? 'Lead source' : `Source 0${index + 1}`;
        const footerLabel = isFeatured ? 'Most complete match' : 'External link';
        const ctaLabel = isFeatured ? 'Read full coverage' : 'Read story';

        const metaChips = (
          <div className="flex flex-wrap items-center gap-2">
            <span className="rounded-full bg-accent px-2.5 py-1 text-[10px] font-semibold uppercase tracking-[0.14em] text-white/95">
              {eyebrow}
            </span>
            <span className="inline-flex items-center gap-1 rounded-full border border-line bg-canvas-raised/80 px-2.5 py-1 text-[11px] font-medium text-ink-muted backdrop-blur-sm">
              <SourceBadgeIcon source={source} domain={domain} />
              <span className="max-w-[180px] truncate">{domain}</span>
            </span>
            {publishedLabel ? (
              <span className="inline-flex items-center gap-1 rounded-full border border-line/80 bg-canvas-sunken/70 px-2.5 py-1 text-[11px] text-ink-muted">
                <Clock3 className="h-3 w-3" strokeWidth={1.8} />
                {publishedLabel}
              </span>
            ) : null}
          </div>
        );

        const body = (
          <div className="space-y-2.5">
            <div className="flex items-start justify-between gap-3">
              <h3 className={isFeatured
                ? 'text-[22px] font-semibold leading-[1.15] tracking-[-0.025em] text-ink md:text-[26px]'
                : 'text-[16px] font-semibold leading-[1.25] tracking-[-0.02em] text-ink'}>
                {title}
              </h3>
              {!isFeatured ? (
                <span className="mt-1 inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-full border border-line bg-canvas-raised text-accent transition group-hover:border-accent group-hover:bg-accent group-hover:text-white">
                  <ArrowUpRight className="h-3.5 w-3.5" strokeWidth={2} />
                </span>
              ) : null}
            </div>
            {excerpt ? (
              <p className={isFeatured
                ? 'max-w-[52ch] text-[14px] leading-6 text-ink-muted md:text-[15px]'
                : 'text-[13px] leading-[1.45] text-ink-muted'}>
                {excerpt}
              </p>
            ) : null}
          </div>
        );

        const footer = (
          <div className="mt-3 flex items-center justify-between gap-3 border-t border-line/80 pt-3">
            <div className="text-[11px] uppercase tracking-[0.08em] text-ink-subtle">{footerLabel}</div>
            <div className="inline-flex items-center gap-1 text-[12px] font-medium text-accent transition group-hover:gap-1.5">
              <span>{ctaLabel}</span>
              <ArrowUpRight className="h-3.5 w-3.5" strokeWidth={2} />
            </div>
          </div>
        );

        return (
          <a
            key={source.url}
            href={source.url}
            target="_blank"
            rel="noopener noreferrer"
            className={[
              'group relative isolate flex h-full flex-col overflow-hidden rounded-[28px] border border-line/80',
              'bg-[radial-gradient(circle_at_top_left,rgba(217,119,87,0.16),transparent_45%),linear-gradient(180deg,rgba(255,255,255,0.98),rgba(251,250,247,0.98))]',
              'shadow-[0_18px_44px_rgba(28,27,25,0.08)] transition duration-300 hover:-translate-y-1 hover:border-line-strong',
              'dark:bg-[radial-gradient(circle_at_top_left,rgba(232,144,105,0.20),transparent_48%),linear-gradient(180deg,rgba(22,22,21,0.98),rgba(14,14,13,0.98))]',
              'dark:shadow-[0_18px_44px_rgba(0,0,0,0.34)]',
              isFeatured ? 'md:col-span-2' : '',
            ].join(' ')}
          >
            {isFeatured ? (
              <div className="flex h-full flex-col md:grid md:grid-cols-[minmax(0,1.12fr)_minmax(260px,0.88fr)]">
                <div className="order-2 flex flex-col justify-between p-5 md:order-1 md:p-6">
                  <div className="space-y-3">
                    {metaChips}
                    {body}
                  </div>
                  {footer}
                </div>
                <SourceVisual
                  source={source}
                  domain={domain}
                  eyebrow={eyebrow}
                  featured
                />
              </div>
            ) : (
              <div className="flex h-full flex-col">
                <SourceVisual
                  source={source}
                  domain={domain}
                  eyebrow={eyebrow}
                  featured={false}
                />
                <div className="flex flex-1 flex-col gap-2.5 p-4">
                  {metaChips}
                  {body}
                </div>
              </div>
            )}
          </a>
        );
      })}
      </div>
    </section>
  );
}

function SourceVisual({
  source,
  domain,
  eyebrow,
  featured,
}: {
  source: ChatMessageSource;
  domain: string;
  eyebrow: string;
  featured: boolean;
}) {
  const [imageFailed, setImageFailed] = useState(false);

  if (source.thumbnail && !imageFailed) {
    return (
      <div
        className={featured
          ? 'order-1 overflow-hidden border-b border-line/70 bg-canvas-sunken md:order-2 md:border-b-0 md:border-l md:border-line/70'
          : 'overflow-hidden border-b border-line/70 bg-canvas-sunken'}
      >
        <div className={featured ? 'relative h-full min-h-[220px]' : 'relative aspect-[2/1]'}>
          <img
            src={source.thumbnail}
            alt=""
            loading="lazy"
            onError={() => setImageFailed(true)}
            className="h-full w-full object-cover transition duration-500 group-hover:scale-[1.04]"
          />
          {featured ? (
            <>
              <div className="absolute inset-0 bg-gradient-to-t from-black/55 via-black/10 to-transparent" />
              <div className="absolute inset-x-0 bottom-0 flex items-end justify-between gap-3 p-4 text-white">
                <div>
                  <div className="mb-1 text-[10px] font-semibold uppercase tracking-[0.14em] text-white/80">
                    {eyebrow}
                  </div>
                  <div className="text-[13px] font-medium leading-5 text-white/95">{domain}</div>
                </div>
              </div>
            </>
          ) : null}
        </div>
      </div>
    );
  }

  const monogram = (domain.replace(/^www\./, '')[0] || '?').toUpperCase();

  return (
    <div
      className={[
        featured ? 'order-1 min-h-[220px] md:order-2 md:border-l md:border-line/70' : 'aspect-[2/1] border-b border-line/70',
        'relative overflow-hidden bg-[radial-gradient(circle_at_top_left,rgba(217,119,87,0.22),transparent_55%),linear-gradient(135deg,rgba(250,237,228,0.95),rgba(245,243,238,0.98))]',
        'dark:bg-[radial-gradient(circle_at_top_left,rgba(232,144,105,0.22),transparent_55%),linear-gradient(135deg,rgba(42,28,20,0.95),rgba(14,14,13,0.98))]',
      ].join(' ')}
      aria-hidden
    >
      <div className="absolute -right-12 -top-10 h-44 w-44 rounded-full bg-accent/10 blur-[2px]" />
      <div className="absolute -bottom-8 -left-6 h-28 w-28 rounded-full border border-line-strong/30" />
      <div className="relative flex h-full items-center justify-center">
        <div className="flex h-16 w-16 items-center justify-center rounded-2xl border border-line/80 bg-canvas-raised/90 text-[26px] font-semibold tracking-[-0.04em] text-accent shadow-[0_10px_28px_rgba(28,27,25,0.10)] backdrop-blur-sm">
          {monogram}
        </div>
      </div>
    </div>
  );
}

function SourceBadgeIcon({ source, domain }: { source: ChatMessageSource; domain: string }) {
  if (source.favicon) {
    return (
      <img
        src={source.favicon}
        alt=""
        className="h-4 w-4 shrink-0 rounded-sm border border-line bg-canvas-raised object-contain"
      />
    );
  }

  return (
    <span className="flex h-4 w-4 shrink-0 items-center justify-center rounded-sm bg-canvas-sunken text-ink-subtle">
      <Globe className="h-3 w-3" aria-hidden />
      <span className="sr-only">{domain}</span>
    </span>
  );
}

function fallbackTitle(url: string, domain?: string | null) {
  try {
    const parsed = new URL(url);
    return domain || parsed.hostname.replace(/^www\./, '');
  } catch {
    return domain || url;
  }
}

function fallbackDomain(url: string) {
  try {
    return new URL(url).hostname.replace(/^www\./, '');
  } catch {
    return url;
  }
}

function truncate(text: string, maxChars: number) {
  if (!text || text.length <= maxChars) return text;
  return `${text.slice(0, maxChars - 1).trimEnd()}...`;
}

function formatPublishedAt(value?: string | null) {
  if (!value) return null;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return null;

  const today = new Date();
  const daysBetween = Math.floor((today.setHours(0, 0, 0, 0) - new Date(parsed).setHours(0, 0, 0, 0)) / 86400000);
  if (daysBetween === 0) return 'Today';
  if (daysBetween === 1) return 'Yesterday';

  return new Intl.DateTimeFormat(undefined, {
    month: 'short',
    day: 'numeric',
  }).format(parsed);
}