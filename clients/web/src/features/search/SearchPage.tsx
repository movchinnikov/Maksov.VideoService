import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { api, ApiError } from '../../shared/api/client';
import type { UserSummary } from '../../shared/api/types';
import { useDebounced, useMe } from '../../shared/lib/data';
import { normalizeQuery } from '../../shared/lib/social';
import { profilePath } from '../../shared/lib/urls';
import { Avatar } from '../../shared/ui/Avatar';
import { FollowButton } from '../../shared/ui/FollowButton';

type Result = { query: string; items: UserSummary[] } | { query: string; error: string };

export function SearchPage() {
  const [raw, setRaw] = useState('');
  const query = normalizeQuery(useDebounced(raw, 300));
  const [result, setResult] = useState<Result | null>(null);
  const me = useMe();

  useEffect(() => {
    if (!query) return;
    const controller = new AbortController();
    api.searchUsers(query, controller.signal).then(
      (r) => setResult({ query, items: r.items }),
      (e: unknown) => {
        if (!controller.signal.aborted) setResult({ query, error: e instanceof ApiError ? e.message : 'Search is not responding.' });
      },
    );
    return () => controller.abort();
  }, [query]);

  const current = query && result?.query === query ? result : null;

  return (
    <>
      <div className="page-head">
        <h1>Search</h1>
      </div>
      <div className="field search-field">
        <label htmlFor="q">Find a person</label>
        <input id="q" type="text" value={raw} onChange={(e) => setRaw(e.target.value)} autoComplete="off" />
      </div>
      {!query && <p className="data muted">Enter the beginning of a name or username.</p>}
      {query && !current && <p className="data muted">Searching…</p>}
      {current && 'error' in current && (
        <div className="error-line" role="alert">
          {current.error}
        </div>
      )}
      {current && 'items' in current && current.items.length === 0 && <p className="data muted">No one found.</p>}
      {current && 'items' in current && (
        <ul className="people">
          {current.items.map((u) => (
            <li key={u.id} className="people__row">
              <Link to={profilePath(u.id)} className="people__who">
                <Avatar username={u.username} />
                <span>
                  <b>{u.username}</b>
                  <span className="muted people__name">{u.displayName}</span>
                </span>
              </Link>
              {me?.id !== u.id && <FollowButton userId={u.id} initial={u.isFollowing} />}
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
