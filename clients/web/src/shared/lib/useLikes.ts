import { useCallback, useEffect, useRef, useState } from 'react';
import type { LikeInfo } from '../api/types';
import { applyLike, emptyLike } from './likes';

/**
 * Like state for a list of items: one batch call per page, optimistic toggle with rollback.
 * `fetchInfo` and `send` may change between renders; the returned functions stay stable.
 */
export function useLikes(
  fetchInfo: (ids: string[]) => Promise<LikeInfo[]>,
  send: (id: string, on: boolean) => Promise<void>,
) {
  const [map, setMap] = useState<Record<string, LikeInfo>>({});
  const mapRef = useRef(map);
  const fetchRef = useRef(fetchInfo);
  const sendRef = useRef(send);
  useEffect(() => {
    fetchRef.current = fetchInfo;
    sendRef.current = send;
  });

  const commit = useCallback((next: Record<string, LikeInfo>) => {
    mapRef.current = next;
    setMap(next);
  }, []);

  /** Loads ids that are not known yet, in one batch. */
  const load = useCallback(
    (ids: string[]) => {
      const missing = ids.filter((id) => !(id in mapRef.current));
      if (missing.length === 0) return;
      fetchRef.current(missing).then(
        (items) => {
          const next = { ...mapRef.current };
          for (const id of missing) next[id] = next[id] ?? emptyLike(id);
          for (const it of items) next[it.id] = it;
          commit(next);
        },
        () => undefined,
      );
    },
    [commit],
  );

  const set = useCallback(
    (id: string, on: boolean) => {
      const prev = mapRef.current[id] ?? emptyLike(id);
      if (prev.likedByMe === on) return;
      commit({ ...mapRef.current, [id]: applyLike(prev, on) });
      sendRef.current(id, on).catch(() => {
        commit({ ...mapRef.current, [id]: prev });
      });
    },
    [commit],
  );

  const toggle = useCallback(
    (id: string) => set(id, !(mapRef.current[id]?.likedByMe ?? false)),
    [set],
  );

  return { likes: map, load, set, toggle };
}
