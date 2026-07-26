import { useId, useState, type ReactNode } from 'react';
import { HardDrive, Globe } from 'lucide-react';

export interface ProvenanceChipProps {
  /** Source identity, shown first — filename, window title, domain, tool. */
  label: string;
  icon?: ReactNode;
  /** Extra chip classes (e.g. receipt-chip--source). */
  className?: string;
  /** Preview heading. Falls back to the label. */
  title?: string;
  /** Quoted span or result excerpt. Counters fabricated-citation risk. */
  snippet?: string | null;
  /** When this input was captured or read. */
  timestamp?: string | null;
  /** True when this source involved an outbound call. */
  outbound?: boolean;
  /** Free-form scope string (e.g. `SCREEN_READ · active window`). */
  scope?: string | null;
  /** Invoked on click/Enter — opens the underlying source. */
  onOpen?: () => void;
}

/**
 * A provenance chip with a layered disclosure: scan the chip, verify on
 * hover/focus, open for the full source.
 *
 * The preview is driven by hover AND focus so it is reachable without a mouse,
 * and it is wired via `aria-describedby` rather than a tooltip attribute so
 * screen readers get the snippet and boundary rather than just the label.
 */
export function ProvenanceChip({
  label,
  icon,
  className = '',
  title,
  snippet,
  timestamp,
  outbound = false,
  scope,
  onOpen,
}: ProvenanceChipProps) {
  const [open, setOpen] = useState(false);
  const previewId = useId();
  const hasPreview = Boolean(snippet || timestamp || scope);

  return (
    <span className="relative inline-flex">
      <button
        type="button"
        className={`receipt-chip ${onOpen ? 'hover:border-line-strong hover:text-ink' : ''} ${className}`}
        aria-label={`Source: ${label}`}
        aria-describedby={hasPreview && open ? previewId : undefined}
        aria-expanded={hasPreview ? open : undefined}
        onClick={onOpen}
        disabled={!onOpen}
        onMouseEnter={() => setOpen(true)}
        onMouseLeave={() => setOpen(false)}
        onFocus={() => setOpen(true)}
        onBlur={() => setOpen(false)}
      >
        {icon}
        {label}
      </button>

      {hasPreview && open ? (
        <span
          id={previewId}
          role="tooltip"
          className="provenance-preview"
        >
          <span className="block text-[11px] font-semibold text-ink">{title || label}</span>
          {scope ? (
            <span className="mt-1 block break-words font-mono text-[10px] text-ink-muted">{scope}</span>
          ) : null}
          {snippet ? (
            <span className="mt-1.5 block max-h-24 overflow-hidden text-[11px] leading-5 text-ink-muted">
              “{snippet.length > 220 ? `${snippet.slice(0, 217)}…` : snippet}”
            </span>
          ) : null}
          <span className="mt-2 flex items-center gap-2 text-[10px] text-ink-subtle">
            <span className="inline-flex items-center gap-1">
              {outbound ? <Globe className="h-3 w-3" /> : <HardDrive className="h-3 w-3" />}
              {outbound ? 'Left this machine' : 'Stayed local'}
            </span>
            {timestamp ? <span>{timestamp}</span> : null}
          </span>
        </span>
      ) : null}
    </span>
  );
}
