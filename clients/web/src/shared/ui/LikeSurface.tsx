import { useRef, useState } from 'react';
import type { MouseEvent, ReactNode } from 'react';
import { isDoubleTap } from '../lib/likes';

/** Wraps media: a double click or double tap likes and shows a brief heart. */
export function LikeSurface({ onDoubleLike, children }: { onDoubleLike: () => void; children: ReactNode }) {
  const last = useRef<number | null>(null);
  const [burst, setBurst] = useState(0);

  const onClickCapture = (e: MouseEvent) => {
    const now = e.timeStamp;
    if (isDoubleTap(last.current, now)) {
      last.current = null;
      onDoubleLike();
      setBurst((n) => n + 1);
    } else {
      last.current = now;
    }
  };

  return (
    <div className="like-surface" onClickCapture={onClickCapture}>
      {children}
      {burst > 0 && (
        <svg key={burst} className="heart-burst" width="96" height="96" viewBox="0 0 24 24" aria-hidden="true">
          <path
            d="M12 21s-7.5-4.6-9.6-9.3C.9 8.2 2.8 4.5 6.4 4.5c2.1 0 3.8 1.2 5.6 3.2 1.8-2 3.5-3.2 5.6-3.2 3.6 0 5.5 3.7 4 7.2C19.5 16.4 12 21 12 21z"
            fill="currentColor"
          />
        </svg>
      )}
    </div>
  );
}
