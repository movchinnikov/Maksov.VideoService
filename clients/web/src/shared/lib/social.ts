import type { VideoStatus } from '../api/types';

export const COMMENT_MAX = 500;

export type CommentCheck = 'empty' | 'too_long' | 'ok';

export function checkComment(text: string): CommentCheck {
  const t = text.trim();
  if (t.length === 0) return 'empty';
  return text.length > COMMENT_MAX ? 'too_long' : 'ok';
}

/** An empty search query is not sent to the server. */
export function normalizeQuery(q: string): string {
  return q.trim().replace(/\s+/g, ' ');
}

/** Label over the thumbnail in the profile grid: none for ready videos. */
export function thumbLabel(status: VideoStatus): string | null {
  switch (status) {
    case 'ready':
      return null;
    case 'failed':
      return 'rejected';
    default:
      return 'editing…';
  }
}

export function initial(username: string): string {
  return (username.trim().charAt(0) || '?').toUpperCase();
}

/** In-memory cache: one request per key; failures and “not ready yet” are evicted from the cache. */
export function memoPromise<T>(
  store: Map<string, Promise<T>>,
  key: string,
  load: () => Promise<T>,
  keep: (v: T) => boolean = () => true,
): Promise<T> {
  const hit = store.get(key);
  if (hit) return hit;
  const p = load().then(
    (v) => {
      if (!keep(v)) store.delete(key);
      return v;
    },
    (e: unknown) => {
      store.delete(key);
      throw e;
    },
  );
  store.set(key, p);
  return p;
}
