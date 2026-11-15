import { useState } from 'react';
import { api } from '../api/client';

/** Follow with an optimistic toggle and rollback on error. */
export function FollowButton({
  userId,
  initial,
  onChange,
}: {
  userId: string;
  initial: boolean;
  onChange?: (following: boolean) => void;
}) {
  const [following, setFollowing] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  const toggle = async () => {
    const next = !following;
    setFollowing(next);
    setFailed(false);
    setBusy(true);
    onChange?.(next);
    try {
      await (next ? api.follow(userId) : api.unfollow(userId));
    } catch {
      setFollowing(!next);
      setFailed(true);
      onChange?.(!next);
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <button
        type="button"
        className={following ? 'btn btn--ghost' : 'btn'}
        onClick={() => void toggle()}
        disabled={busy}
        aria-pressed={following}
      >
        {following ? 'Following' : 'Follow'}
      </button>
      {failed && (
        <span className="data" role="alert">
          {' '}
          failed
        </span>
      )}
    </>
  );
}
