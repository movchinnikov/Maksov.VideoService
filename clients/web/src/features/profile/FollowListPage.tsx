import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router';
import { api, ApiError } from '../../shared/api/client';
import { useMe } from '../../shared/lib/data';
import { buildFollowRows } from '../../shared/lib/friends';
import type { FollowRow } from '../../shared/lib/friends';
import { mergePages } from '../../shared/lib/pages';
import { profilePath } from '../../shared/lib/urls';
import { Avatar } from '../../shared/ui/Avatar';
import { FollowButton } from '../../shared/ui/FollowButton';
import { RelationBadge } from '../../shared/ui/RelationBadge';

type Kind = 'followers' | 'following';

interface Page {
  rows: FollowRow[];
  next: string | null;
  total: number;
}

async function loadPage(id: string, kind: Kind, cursor?: string, signal?: AbortSignal): Promise<Page> {
  const page = await api.getFollowList(id, kind, cursor, signal);
  const users = await api.getUsersByIds(page.items.map((i) => i.userId), signal).catch(() => ({ items: [] }));
  return { rows: buildFollowRows(page.items, users.items), next: page.nextCursor, total: page.total };
}

export function FollowListPage({ kind }: { kind: Kind }) {
  const { id = '' } = useParams();
  const me = useMe();
  const [rows, setRows] = useState<FollowRow[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [total, setTotal] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [more, setMore] = useState(false);
  const title = kind === 'followers' ? 'Followers' : 'Following';

  useEffect(() => {
    const controller = new AbortController();
    loadPage(id, kind, undefined, controller.signal).then(
      (p) => {
        setRows(p.rows);
        setNext(p.next);
        setTotal(p.total);
      },
      (e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof ApiError ? e.message : 'Could not load the list.');
      },
    );
    return () => controller.abort();
  }, [id, kind]);

  const loadMore = () => {
    if (!next || more) return;
    setMore(true);
    loadPage(id, kind, next).then(
      (p) => {
        setRows((prev) => mergePages(prev ?? [], p.rows));
        setNext(p.next);
        setTotal(p.total);
        setMore(false);
      },
      () => setMore(false),
    );
  };

  // Optimistic local update; mutual is derived from followsYou and the new follow state.
  const setFollowing = (userId: string, following: boolean) =>
    setRows((prev) => (prev ?? []).map((r) => (r.id === userId ? { ...r, isFollowing: following } : r)));

  return (
    <>
      <div className="follow-head">
        <h1>{title}</h1>
        <span className="data muted">
          {rows ? total : ''} <Link to={profilePath(id)}>Back to profile</Link>
        </span>
      </div>
      {error && (
        <div className="error-line" role="alert">
          {error}
        </div>
      )}
      {!rows && !error && <p className="data muted">Loading…</p>}
      {rows && rows.length === 0 && (
        <p className="data muted empty-grid">{kind === 'followers' ? 'No followers yet.' : 'Not following anyone yet.'}</p>
      )}
      {rows && rows.length > 0 && (
        <ul className="people">
          {rows.map((r) => (
            <li key={r.id} className="people__row">
              <Link to={profilePath(r.id)} className="people__who">
                <Avatar username={r.username} />
                <span>
                  <b>{r.displayName || r.username}</b>
                  <RelationBadge followsYou={r.followsYou} isFollowing={r.isFollowing} />
                  <span className="muted people__name">@{r.username}</span>
                </span>
              </Link>
              {me?.id !== r.id && (
                <FollowButton userId={r.id} initial={r.isFollowing} onChange={(f) => setFollowing(r.id, f)} />
              )}
            </li>
          ))}
        </ul>
      )}
      {next && (
        <p className="more">
          <button type="button" className="btn btn--ghost" onClick={loadMore} disabled={more}>
            {more ? 'Loading…' : 'Load more'}
          </button>
        </p>
      )}
    </>
  );
}
