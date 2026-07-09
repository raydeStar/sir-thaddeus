interface ThaddeusSignetProps {
  className?: string;
}

/** Compact app identity. The raven remains a narrative familiar. */
export function ThaddeusSignet({ className = 'h-9 w-9' }: ThaddeusSignetProps) {
  return (
    <svg viewBox="0 0 32 32" className={className} aria-hidden data-testid="thaddeus-signet">
      <circle cx="16" cy="16" r="15" fill="#07111d" stroke="#c79239" strokeWidth="1" />
      <circle cx="16" cy="16" r="12.5" fill="none" stroke="#f5ebd8" strokeOpacity="0.16" />
      <text x="11.8" y="20.8" textAnchor="middle" fill="#f5ebd8" fontFamily="Georgia, 'Times New Roman', serif" fontSize="15" fontWeight="700">S</text>
      <text x="20.3" y="20.8" textAnchor="middle" fill="#d8a447" fontFamily="Georgia, 'Times New Roman', serif" fontSize="15" fontWeight="700">T</text>
      <path d="M15 15.6 16 14.5 17 15.6 16 16.7Z" fill="#f08b68" />
      <path d="M13.2 27.1 16 25.6 18.8 27.1 16 28.6Z" fill="#d8a447" />
    </svg>
  );
}
