import { describe, expect, it } from 'vitest';
import { applyLike, chunk, emptyLike, formatCount, isDoubleTap } from './likes';

describe('likes helpers', () => {
  it('chunks ids', () => {
    expect(chunk([1, 2, 3, 4, 5], 2)).toEqual([[1, 2], [3, 4], [5]]);
    expect(chunk([], 50)).toEqual([]);
  });
  it('applies like and unlike', () => {
    const base = { id: 'a', count: 2, likedByMe: false };
    expect(applyLike(base, true)).toEqual({ id: 'a', count: 3, likedByMe: true });
    expect(applyLike({ ...base, likedByMe: true }, false)).toEqual({ id: 'a', count: 1, likedByMe: false });
  });
  it('is idempotent and never negative', () => {
    const liked = { id: 'a', count: 1, likedByMe: true };
    expect(applyLike(liked, true)).toBe(liked);
    expect(applyLike({ id: 'a', count: 0, likedByMe: true }, false).count).toBe(0);
  });
  it('formats counts', () => {
    expect(formatCount(0)).toBe('0');
    expect(formatCount(999)).toBe('999');
    expect(formatCount(1200)).toBe('1.2K');
    expect(formatCount(1000)).toBe('1K');
    expect(formatCount(25_000)).toBe('25K');
    expect(formatCount(3_400_000)).toBe('3.4M');
  });
  it('detects double tap', () => {
    expect(isDoubleTap(null, 1000)).toBe(false);
    expect(isDoubleTap(1000, 1200)).toBe(true);
    expect(isDoubleTap(1000, 1400)).toBe(false);
  });
  it('empty like', () => expect(emptyLike('x')).toEqual({ id: 'x', count: 0, likedByMe: false }));
});
