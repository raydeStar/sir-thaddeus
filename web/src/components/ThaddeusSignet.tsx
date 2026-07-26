interface ThaddeusSignetProps {
  className?: string;
}

/**
 * App identity mark.
 *
 * Deliberately quiet: one geometric form, one stroke weight, and no baked-in
 * palette. Colour comes from `currentColor` and the theme accent, so the mark
 * recedes into whatever surface hosts it instead of competing with the content.
 *
 * The previous emblem stacked two concentric rings, a two-tone serif monogram,
 * and two diamonds into 32px using five hardcoded hex values — it read as a
 * heraldic crest and was the loudest object in a calm tech shell.
 *
 * Placeholder pending real branding: container plus monogram, nothing else.
 */
export function ThaddeusSignet({ className = 'h-9 w-9' }: ThaddeusSignetProps) {
  return (
    <svg
      viewBox="0 0 32 32"
      className={className}
      aria-hidden
      data-testid="thaddeus-signet"
      fill="none"
    >
      {/* Rounded container: a single hairline that borrows the surrounding text
          colour at low opacity, so it never out-contrasts adjacent labels. */}
      <rect
        x="2.75"
        y="2.75"
        width="26.5"
        height="26.5"
        rx="8.5"
        stroke="currentColor"
        strokeOpacity="0.28"
        strokeWidth="1.5"
      />
      {/* Geometric monogram. Grotesque letterforms in the UI's own family keep
          the mark in the same typographic voice as the rest of the shell. */}
      <text
        x="16"
        y="21.1"
        textAnchor="middle"
        fill="currentColor"
        fontFamily="Inter, -apple-system, 'Segoe UI', system-ui, sans-serif"
        fontSize="12.5"
        fontWeight="600"
        letterSpacing="-0.6"
      >
        ST
      </text>
    </svg>
  );
}
