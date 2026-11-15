import { useEffect, useRef, useState } from 'react';
import { api } from '../api/client';
import type { Description, Profile } from '../api/types';
import { memoPromise } from './social';

const meStore = new Map<string, Promise<Profile>>();
const descStore = new Map<string, Promise<Description | null>>();

export const getMeCached = () => memoPromise(meStore, 'me', () => api.getMe());
export const invalidateMe = () => meStore.clear();

/** The description is cached only in terminal states. */
export const getDescriptionCached = (id: string) =>
  memoPromise(descStore, id, () => api.getDescription(id), (d) => d?.status === 'done' || d?.status === 'failed');

export function forgetDescription(id: string) {
  descStore.delete(id);
}

/** Current user (from cache); null until loaded or on error. */
export function useMe(): Profile | null {
  const [me, setMe] = useState<Profile | null>(null);
  useEffect(() => {
    let alive = true;
    getMeCached().then(
      (m) => alive && setMe(m),
      () => undefined,
    );
    return () => {
      alive = false;
    };
  }, []);
  return me;
}

/** true once the element first enters the viewport (we stop watching after that). */
export function useInView<T extends Element>(): [React.RefObject<T | null>, boolean] {
  const ref = useRef<T | null>(null);
  const [seen, setSeen] = useState(false);
  useEffect(() => {
    const el = ref.current;
    if (!el || seen) return;
    if (typeof IntersectionObserver === 'undefined') {
      const t = window.setTimeout(() => setSeen(true), 0);
      return () => window.clearTimeout(t);
    }
    const io = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setSeen(true);
          io.disconnect();
        }
      },
      { rootMargin: '200px' },
    );
    io.observe(el);
    return () => io.disconnect();
  }, [seen]);
  return [ref, seen];
}

export function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setV(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return v;
}
