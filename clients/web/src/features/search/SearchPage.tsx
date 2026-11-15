import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { api, ApiError } from '../../shared/api/client';
import { useDebounced, useMe } from '../../shared/lib/data';
import { joinRecommendations, mutualLabel, withFollowing } from '../../shared/lib/friends';
import type { PersonRow } from '../../shared/lib/friends';
import { normalizeQuery } from '../../shared/lib/social';
import { profilePath } from '../../shared/lib/urls';
import { Avatar } from '../../shared/ui/Avatar';
import { FollowButton } from '../../shared/ui/FollowButton';
import { RelationBadge } from '../../shared/ui/RelationBadge';

type Result = { query: string; items: PersonRow[] } | { query: string; error: string };

export function SearchPage() {
  const [raw, setRaw] = useState('');
  const query = normalizeQuery(useDebounced(raw, 300));
  const [result, setResult] = useState<Result | null>(null);
  const [suggested, setSuggested] = useState<PersonRow[] | null>(null);
  const me = useMe();

  useEffect(() => {
    if (!query) return;
    const controller = new AbortController();
    api
      .searchUsers(query, controller.signal)
      .then(async (r) => {
        const status = await api.getFollowingStatus(r.items.map((u) => u.id), controller.signal).catch(() => ({ following: [], followedBy: [] as string[] }));
        setResult({ query, items: withFollowing(r.items, status.following, status.followedBy ?? []) });
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setResult({ query, error: e instanceof ApiError ? e.message : 'Search is not responding.' });
      });
    return () => controller.abort();
  }, [query]);

  useEffect(() => {
    if (query) return;
    const controller = new AbortController();
    api
      .getRecommendations(controller.signal)
      .then(async (r) => {
        const users = await api.getUsersByIds(r.items.map((x) => x.userId), controller.signal);
        setSuggested(joinRecommendations(r.items, users.items));
      })
      .catch(() => {
        if (!controller.signal.aborted) setSuggested([]);
      });
    return () => controller.abort();
  }, [query]);

  const current = query && result?.query === query ? result : null;

  const list = (rows: PersonRow[]) => (
    <ul className="people">
      {rows.map((u) => (
        <li key={u.id} className="people__row">
          <Link to={profilePath(u.id)} className="people__who">
            <Avatar username={u.username} />
            <span>
              <b>{u.username}</b>
              <RelationBadge followsYou={u.followsYou} isFollowing={u.isFollowing} />
              <span className="muted people__name">{u.displayName}</span>
              {u.mutualFriends !== undefined && u.mutualFriends > 0 && (
                <span className="muted people__name data">{mutualLabel(u.mutualFriends)}</span>
              )}
            </span>
          </Link>
          {me?.id !== u.id && <FollowButton userId={u.id} initial={u.isFollowing} />}
        </li>
      ))}
    </ul>
  );

  return (
    <>
      <div className="page-head">
        <h1>Search</h1>
      </div>
      <div className="field search-field">
        <label htmlFor="q">Find a person</label>
        <input id="q" type="text" value={raw} onChange={(e) => setRaw(e.target.value)} autoComplete="off" />
      </div>
      {!query && (
        <section aria-labelledby="suggested-h">
          <h2 id="suggested-h" className="section-label">
            Suggested for you
          </h2>
          {suggested === null && <p className="data muted">Loading…</p>}
          {suggested !== null && suggested.length === 0 && <p className="data muted">Type the start of a name or login.</p>}
          {suggested !== null && suggested.length > 0 && list(suggested)}
        </section>
      )}
      {query && !current && <p className="data muted">Searching…</p>}
      {current && 'error' in current && (
        <div className="error-line" role="alert">
          {current.error}
        </div>
      )}
      {current && 'items' in current && current.items.length === 0 && <p className="data muted">Nobody found.</p>}
      {current && 'items' in current && list(current.items)}
    </>
  );
}
