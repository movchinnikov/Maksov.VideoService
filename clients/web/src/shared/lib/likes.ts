import type { LikeInfo } from '../api/types';

export const LIKE_BATCH_MAX = 50;

export function chunk<T>(items: T[], size: number): T[][] {
  const out: T[][] = [];
  for (let i = 0; i < items.length; i += size) out.push(items.slice(i, i + size));
  return out;
}

/** Apply a like/unlike locally. Idempotent: no change when the state already matches. */
export function applyLike(info: LikeInfo, on: boolean): LikeInfo {
  if (info.likedByMe === on) return info;
  return { ...info, likedByMe: on, count: Math.max(0, info.count + (on ? 1 : -1)) };
}

/** 1200 -> "1.2K", 3400000 -> "3.4M". */
export function formatCount(n: number): string {
  if (n < 1000) return String(n);
  if (n < 1_000_000) return `${trim(n / 1000)}K`;
  return `${trim(n / 1_000_000)}M`;
}

function trim(v: number): string {
  const s = v >= 100 ? v.toFixed(0) : v.toFixed(1);
  return s.replace(/\.0$/, '');
}

/** Two clicks within the window count as a double tap. */
export function isDoubleTap(prev: number | null, now: number, windowMs = 300): boolean {
  return prev !== null && now - prev > 0 && now - prev <= windowMs;
}

export function emptyLike(id: string): LikeInfo {
  return { id, count: 0, likedByMe: false };
}
