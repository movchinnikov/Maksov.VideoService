import { relationBadge } from '../lib/friends';

export function RelationBadge({ followsYou, isFollowing }: { followsYou: boolean | undefined; isFollowing: boolean }) {
  const text = relationBadge(followsYou, isFollowing);
  return text ? <span className="relation data">{text}</span> : null;
}
