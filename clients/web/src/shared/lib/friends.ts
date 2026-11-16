import type { FollowListItem, Recommendation, UserSummary } from '../api/types';

export interface PersonRow extends UserSummary {
  isFollowing: boolean;
  followsYou?: boolean;
  mutualFriends?: number;
}

/** Attach follow state to search results. */
export function withFollowing(users: UserSummary[], following: string[], followedBy: string[] = []): PersonRow[] {
  const set = new Set(following);
  const by = new Set(followedBy);
  return users.map((u) => ({ ...u, isFollowing: set.has(u.id), followsYou: by.has(u.id) }));
}

/** Relationship badge text relative to the viewer, or null. */
export function relationBadge(followsYou: boolean | undefined, isFollowing: boolean): 'Mutual' | 'Follows you' | null {
  if (!followsYou) return null;
  return isFollowing ? 'Mutual' : 'Follows you';
}

export interface FollowRow {
  id: string;
  username: string;
  displayName: string;
  isFollowing: boolean;
  followsYou: boolean;
}

/** Join a follow-list page with batch-loaded profiles; unknown profiles fall back to the id. */
export function buildFollowRows(items: FollowListItem[], users: UserSummary[]): FollowRow[] {
  const byId = new Map(users.map((u) => [u.id, u]));
  return items.map((i) => {
    const u = byId.get(i.userId);
    return {
      id: i.userId,
      username: u?.username ?? i.userId,
      displayName: u?.displayName ?? '',
      isFollowing: i.isFollowing,
      followsYou: i.followsYou,
    };
  });
}

/** Join recommendations with loaded profiles; keeps recommendation order, skips unknown profiles. */
export function joinRecommendations(recs: Recommendation[], users: UserSummary[]): PersonRow[] {
  const byId = new Map(users.map((u) => [u.id, u]));
  const rows: PersonRow[] = [];
  for (const r of recs) {
    const u = byId.get(r.userId);
    if (u) rows.push({ ...u, isFollowing: false, mutualFriends: r.mutualFriends });
  }
  return rows;
}

/** Rows for the likers list: profiles and relation flags joined onto like items. */
export function buildLikerRows(
  items: { userId: string }[],
  users: UserSummary[],
  following: string[],
  followedBy: string[],
): FollowRow[] {
  const byId = new Map(users.map((u) => [u.id, u]));
  const f = new Set(following);
  const b = new Set(followedBy);
  return items.map((i) => {
    const u = byId.get(i.userId);
    return {
      id: i.userId,
      username: u?.username ?? i.userId,
      displayName: u?.displayName ?? '',
      isFollowing: f.has(i.userId),
      followsYou: b.has(i.userId),
    };
  });
}

export function mutualLabel(n: number): string {
  return `${n} mutual ${n === 1 ? 'friend' : 'friends'}`;
}
