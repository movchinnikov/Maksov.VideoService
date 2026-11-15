import { describe, expect, it } from 'vitest';
import { buildFollowRows, joinRecommendations, mutualLabel, relationBadge, withFollowing } from './friends';

const u = (id: string) => ({ id, username: id, displayName: id, bio: '' });

describe('friends helpers', () => {
  it('merges follow status', () => {
    const rows = withFollowing([u('a'), u('b')], ['b']);
    expect(rows.map((r) => r.isFollowing)).toEqual([false, true]);
  });
  it('joins recommendations in order and skips unknown', () => {
    const rows = joinRecommendations(
      [
        { userId: 'b', mutualFriends: 2, similarity: 0.1 },
        { userId: 'x', mutualFriends: 1, similarity: 0.1 },
        { userId: 'a', mutualFriends: 1, similarity: 0.2 },
      ],
      [u('a'), u('b')],
    );
    expect(rows.map((r) => r.id)).toEqual(['b', 'a']);
    expect(rows[0]?.mutualFriends).toBe(2);
  });
  it('pluralizes mutual label', () => {
    expect(mutualLabel(1)).toBe('1 mutual friend');
    expect(mutualLabel(3)).toBe('3 mutual friends');
  });
});

describe('relationBadge and follow rows', () => {
  it('badge', () => {
    expect(relationBadge(true, true)).toBe('Mutual');
    expect(relationBadge(true, false)).toBe('Follows you');
    expect(relationBadge(false, true)).toBeNull();
    expect(relationBadge(undefined, false)).toBeNull();
  });
  it('withFollowing uses followedBy', () => {
    const rows = withFollowing([u('a'), u('b')], ['a'], ['a', 'b']);
    expect(rows.map((r) => [r.isFollowing, r.followsYou])).toEqual([[true, true], [false, true]]);
  });
  it('builds follow rows with fallback', () => {
    const rows = buildFollowRows(
      [
        { userId: 'a', followedAt: '', isFollowing: true, followsYou: false, isMutual: false },
        { userId: 'z', followedAt: '', isFollowing: false, followsYou: true, isMutual: false },
      ],
      [u('a')],
    );
    expect(rows[0]).toMatchObject({ id: 'a', username: 'a', isFollowing: true });
    expect(rows[1]).toMatchObject({ id: 'z', username: 'z', displayName: '', followsYou: true });
  });
});
