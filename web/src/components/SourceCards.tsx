import { useCallback, useState } from 'react';
import type { ChatMessageSource } from '@thaddeus/shared-types';
import { ArrowUpRight, Clock3, Globe, Sparkles } from 'lucide-react';
import { openExternalUrl } from '../lib/externalLinks';

interface SourceCardsProps {
  sources: readonly ChatMessageSource[];
}

// Tuned so a typical web_search reply (5 results) shows everything at once,
// but bigger result sets fold behind a single click instead of overwhelming
// the message.
const COLLAPSED_LIMIT = 6;

export function SourceCards({ sources }: SourceCardsProps) {
  const [imageFailures, setImageFailures] = useState<Record<string, true>>({});
  const [expanded, setExpanded] = useState(false);

  const markImageFailed = useCallback((key: string) => {
    setImageFailures((current) => ({ ...current, [key]: true }));
  }, []);

  if (sources.length === 0) return null;

  const overflow = sources.length > COLLAPSED_LIMIT;
  const visible = expanded || !overflow ? sources : sources.slice(0, COLLAPSED_LIMIT);

  return (
    <section className="mt-5" data-testid="chat-source-cards">
      <div className="mb-3 flex items-center gap-3">
        <div className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-2.5 py-1 text-[10px] font-semibold uppercase tracking-[0.12em] text-accent shadow-[0_8px_22px_rgba(28,27,25,0.04)] dark:shadow-[0_8px_22px_rgba(0,0,0,0.28)]">
          <Sparkles className="h-3 w-3" strokeWidth={2} />
          <span>Latest reporting</span>
        </div>
        <div className="h-px flex-1 bg-gradient-to-r from-line-strong via-line to-transparent" />
        <span className="shrink-0 text-[10px] uppercase tracking-[0.08em] text-ink-subtle">
          {sources.length} {sources.length === 1 ? 'source' : 'sources'}
        </span>
      </div>

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 md:grid-cols-3">
        {visible.map((source, index) => {
          const title = source.title?.trim() || fallbackTitle(source.url, source.domain);
          const excerpt = source.excerpt?.trim() || '';
          const domain = source.domain?.trim() || fallbackDomain(source.url);
          const publishedLabel = formatPublishedAt(source.publishedAt);
          const sourceKey = `${source.url}-${index}`;
          const hasThumbnail = Boolean(source.thumbnail && !imageFailures[sourceKey]);
          const featured = index === 0 && visible.length >= 3;
          const compact = index >= 2 && visible.length >= 5;

          return (
            <a
              key={sourceKey}
              href={source.url}
              data-source-card="true"
              onClick={(event) => {
                event.preventDefault();
                void openExternalUrl(source.url);
              }}
              onAuxClick={(event) => {
                event.preventDefault();
                void openExternalUrl(source.url);
              }}
              className={[
                'group/source-card relative flex flex-col overflow-hidden rounded-2xl border border-line/80',
                featured ? 'md:col-span-2' : '',
                compact ? 'min-h-[108px]' : '',
                'bg-canvas-raised/92 text-left shadow-[0_10px_22px_rgba(28,27,25,0.05)] outline-none transition',
                'hover:-translate-y-0.5 hover:border-line-strong hover:shadow-[0_18px_36px_rgba(28,27,25,0.10)]',
                'focus-visible:ring-2 focus-visible:ring-accent/55 dark:bg-canvas-raised/75 dark:shadow-[0_12px_28px_rgba(0,0,0,0.28)]',
              ].join(' ')}
            >
              <SourceImage
                source={source}
                sourceKey={sourceKey}
                domain={domain}
                hasThumbnail={hasThumbnail}
                featured={featured}
                compact={compact}
                index={index}
                onImageFailed={markImageFailed}
              />

              {compact ? (
                <div className="pointer-events-none absolute inset-x-0 bottom-0 z-[1] p-3 text-white">
                  <div className="mb-1 flex min-w-0 items-center gap-1.5 text-[10px] font-medium text-white/75">
                    <span className="truncate">{domain}</span>
                    {publishedLabel ? (
                      <>
                        <span aria-hidden>/</span>
                        <span className="shrink-0">{publishedLabel}</span>
                      </>
                    ) : null}
                  </div>
                  <h3 className="overflow-hidden text-[12.5px] font-semibold leading-[1.25] [display:-webkit-box] [-webkit-box-orient:vertical] [-webkit-line-clamp:2]">
                    {title}
                  </h3>
                </div>
              ) : (
                <div className={featured ? 'flex flex-1 flex-col gap-2 p-3.5' : 'flex flex-1 flex-col gap-2 p-3'}>
                  <div className="flex min-w-0 items-center gap-1.5 text-[11px] font-medium text-ink-muted">
                    <SourceBadgeIcon source={source} domain={domain} />
                    <span className="truncate">{domain}</span>
                    {publishedLabel ? (
                      <>
                        <span className="text-ink-subtle" aria-hidden>/</span>
                        <span className="inline-flex shrink-0 items-center gap-1 text-ink-subtle">
                          <Clock3 className="h-3 w-3" strokeWidth={1.8} />
                          {publishedLabel}
                        </span>
                      </>
                    ) : null}
                  </div>
                  <h3 className={[
                    'overflow-hidden font-semibold leading-[1.3] text-ink [display:-webkit-box] [-webkit-box-orient:vertical]',
                    featured ? 'text-[15px] [-webkit-line-clamp:2]' : 'text-[13px] [-webkit-line-clamp:3]',
                  ].join(' ')}>
                    {title}
                  </h3>
                  {featured && excerpt ? (
                    <p className="overflow-hidden text-[12px] leading-[1.45] text-ink-muted [display:-webkit-box] [-webkit-box-orient:vertical] [-webkit-line-clamp:2]">
                      {excerpt}
                    </p>
                  ) : null}
                </div>
              )}

              <span
                aria-hidden
                className="absolute right-3 top-3 inline-flex h-7 w-7 items-center justify-center rounded-full border border-line/70 bg-canvas/85 text-accent backdrop-blur transition group-hover/source-card:border-accent group-hover/source-card:bg-accent group-hover/source-card:text-white"
              >
                <ArrowUpRight className="h-3.5 w-3.5" strokeWidth={2} />
              </span>
            </a>
          );
        })}
      </div>

      {overflow ? (
        <div className="mt-3 flex justify-center">
          <button
            type="button"
            onClick={() => setExpanded((v) => !v)}
            data-testid="chat-source-cards-toggle"
            className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3 py-1.5 text-[11px] font-medium text-ink-muted transition hover:border-line-strong hover:text-ink"
          >
            {expanded ? 'Show fewer' : `Show all ${sources.length} sources`}
          </button>
        </div>
      ) : null}
    </section>
  );
}

function SourceImage({
  source,
  sourceKey,
  domain,
  hasThumbnail,
  featured,
  compact,
  index,
  onImageFailed,
}: {
  source: ChatMessageSource;
  sourceKey: string;
  domain: string;
  hasThumbnail: boolean;
  featured: boolean;
  compact: boolean;
  index: number;
  onImageFailed: (key: string) => void;
}) {
  const mediaClass = compact ? 'h-[108px]' : featured ? 'h-[150px]' : 'h-[130px]';
  const sourceLabel = `Source ${String(index + 1).padStart(2, '0')}`;

  if (hasThumbnail) {
    return (
      <div className={`relative ${mediaClass} w-full overflow-hidden bg-canvas-sunken`}>
        <img
          data-testid="source-card-thumbnail"
          src={source.thumbnail ?? ''}
          alt=""
          loading="lazy"
          onError={() => onImageFailed(sourceKey)}
          className="h-full w-full object-cover transition duration-500 group-hover/source-card:scale-[1.04]"
        />
        <div className="pointer-events-none absolute inset-0 bg-gradient-to-t from-black/75 via-black/15 to-transparent" />
        {!compact ? (
          <span className="absolute bottom-3 left-3 rounded-full border border-white/20 bg-black/45 px-2 py-1 text-[9px] font-semibold uppercase tracking-[0.14em] text-white/85 backdrop-blur">
            {sourceLabel}
          </span>
        ) : null}
      </div>
    );
  }

  // Fallback: warm radial gradient with the favicon (or a monogram disc) +
  // domain caption. Aim is for the no-image card to still feel deliberate
  // -- a designed empty state rather than a placeholder.
  const cleanDomain = domain.replace(/^www\./, '');
  const monogram = (cleanDomain[0] || '?').toUpperCase();
  return (
    <div className={`relative flex ${mediaClass} w-full items-center justify-center overflow-hidden bg-[radial-gradient(circle_at_30%_20%,rgba(217,119,87,0.16),transparent_55%),radial-gradient(circle_at_70%_80%,rgba(118,143,201,0.14),transparent_55%),linear-gradient(135deg,rgba(255,255,255,0.04),rgba(255,255,255,0))] dark:bg-[radial-gradient(circle_at_30%_20%,rgba(232,144,105,0.22),transparent_55%),radial-gradient(circle_at_70%_80%,rgba(132,160,224,0.18),transparent_55%),linear-gradient(135deg,rgba(255,255,255,0.06),rgba(255,255,255,0))]`}>
      <span className="absolute left-3 top-3 rounded-full border border-line/60 bg-canvas/65 px-2 py-1 text-[9px] font-semibold uppercase tracking-[0.14em] text-ink-muted backdrop-blur">
        {sourceLabel}
      </span>
      <div className="flex flex-col items-center gap-2">
        {source.favicon ? (
          <img
            src={source.favicon}
            alt=""
            className="h-10 w-10 rounded-xl border border-line bg-canvas-raised object-contain p-1.5 shadow-[0_6px_18px_rgba(28,27,25,0.10)]"
          />
        ) : (
          <span className="flex h-10 w-10 items-center justify-center rounded-xl border border-line bg-canvas-raised text-[16px] font-semibold text-accent shadow-[0_6px_18px_rgba(28,27,25,0.10)]">
            {monogram}
          </span>
        )}
        <span className="text-[10px] font-semibold uppercase tracking-[0.14em] text-ink-subtle">
          {cleanDomain}
        </span>
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
