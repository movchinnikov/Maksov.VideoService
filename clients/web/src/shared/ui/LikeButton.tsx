import { formatCount } from '../lib/likes';

function Heart({ filled }: { filled: boolean }) {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path
        d="M12 21s-7.5-4.6-9.6-9.3C.9 8.2 2.8 4.5 6.4 4.5c2.1 0 3.8 1.2 5.6 3.2 1.8-2 3.5-3.2 5.6-3.2 3.6 0 5.5 3.7 4 7.2C19.5 16.4 12 21 12 21z"
        fill={filled ? 'currentColor' : 'none'}
        stroke="currentColor"
        strokeWidth="2"
        strokeLinejoin="miter"
      />
    </svg>
  );
}

export function LikeButton({
  count,
  liked,
  onToggle,
  label,
}: {
  count: number;
  liked: boolean;
  onToggle: () => void;
  label: string;
}) {
  return (
    <button type="button" className={liked ? 'like like--on' : 'like'} onClick={onToggle} aria-pressed={liked} aria-label={label}>
      <Heart filled={liked} />
      <span className="data">{formatCount(count)}</span>
    </button>
  );
}
