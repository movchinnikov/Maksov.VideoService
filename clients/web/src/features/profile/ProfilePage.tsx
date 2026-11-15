import { useCallback, useEffect, useState } from 'react';
import { Link, Navigate, useParams } from 'react-router';
import { useAuth } from '../../app/auth';
import { api, ApiError } from '../../shared/api/client';
import type { FeedItem, Profile, UserCard, Video } from '../../shared/api/types';
import { getMeCached, useMe } from '../../shared/lib/data';
import { isInProgress } from '../../shared/lib/format';
import { mergeFirstPage, mergePages } from '../../shared/lib/pages';
import { thumbLabel } from '../../shared/lib/social';
import { profilePath, videoPath } from '../../shared/lib/urls';
import { usePolling } from '../../shared/lib/usePolling';
import { Avatar } from '../../shared/ui/Avatar';
import { FollowButton } from '../../shared/ui/FollowButton';
import { Poster } from '../../shared/ui/Poster';

/** /profile → own profile. */
export function ProfileRedirect() {
  const me = useMe();
  return me ? <Navigate to={profilePath(me.id)} replace /> : <p className="data muted">Fetching the profile card…</p>;
}

interface Tile {
  id: string;
  title: string;
  poster: string | null;
  status: Video['status'];
}

const fromVideo = (v: Video): Tile => ({ id: v.id, title: v.title, poster: v.playback?.poster ?? null, status: v.status });
const fromFeed = (v: FeedItem): Tile => ({ id: v.id, title: v.title, poster: v.playback.poster, status: 'ready' });

type Head = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ok'; user: UserCard | Profile };

export function ProfilePage() {
  const { id = '' } = useParams();
  const [head, setHead] = useState<Head>({ kind: 'loading' });

  useEffect(() => {
    const controller = new AbortController();
    getMeCached()
      .then((m): Promise<UserCard | Profile> => (m.id === id ? Promise.resolve(m) : api.getUser(id, controller.signal)))
      .then((user) => {
        if (!controller.signal.aborted) setHead({ kind: 'ok', user });
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) {
          setHead({
            kind: 'error',
            message: e instanceof ApiError && e.status === 404 ? 'No such person.' : 'Could not load the profile.',
          });
        }
      });
    return () => controller.abort();
  }, [id]);

  if (head.kind === 'loading') return <p className="data muted">Fetching the profile card…</p>;
  if (head.kind === 'error') {
    return (
      <section className="notice">
        <h1>Profile unavailable</h1>
        <p>{head.message}</p>
      </section>
    );
  }
  return <ProfileBody key={id} id={id} user={head.user} />;
}

function ProfileBody({ id, user }: { id: string; user: UserCard | Profile }) {
  const auth = useAuth();
  const me = useMe();
  const own = me?.id === id;
  const [followers, setFollowers] = useState(user.followersCount ?? 0);
  const [tiles, setTiles] = useState<Tile[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [more, setMore] = useState(false);

  const fetchPage = useCallback(
    (cursor?: string, signal?: AbortSignal) =>
      own
        ? api.listVideos(cursor, signal).then((p) => ({ tiles: p.items.map(fromVideo), next: p.nextCursor }))
        : api.getUserVideos(id, cursor, signal).then((p) => ({ tiles: p.items.map(fromFeed), next: p.nextCursor })),
    [id, own],
  );

  useEffect(() => {
    if (!me) return;
    const controller = new AbortController();
    fetchPage(undefined, controller.signal).then(
      (p) => {
        setTiles(p.tiles);
        setNext(p.next);
      },
      (e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof ApiError ? e.message : 'Could not load posts.');
      },
    );
    return () => controller.abort();
  }, [fetchPage, me]);

  const working = own && !!tiles?.some((t) => isInProgress(t.status));
  usePolling(async (signal) => {
    const p = await fetchPage(undefined, signal);
    setTiles((prev) => mergeFirstPage(prev ?? [], p.tiles));
    return p.tiles.some((t) => isInProgress(t.status));
  }, working);

  const loadMore = () => {
    if (!next || more) return;
    setMore(true);
    fetchPage(next).then(
      (p) => {
        setTiles((prev) => mergePages(prev ?? [], p.tiles));
        setNext(p.next);
        setMore(false);
      },
      () => setMore(false),
    );
  };

  const posts = tiles ? `${tiles.length}${next ? '+' : ''}` : '–';
  const following = 'isFollowing' in user ? user.isFollowing : false;

  return (
    <>
      <section className="profile-head">
        <Avatar username={user.username} size="lg" />
        <div className="profile-head__main">
          <h1>{user.username}</h1>
          {user.displayName && <p className="profile-head__name">{user.displayName}</p>}
          {user.bio && <p className="profile-head__bio">{user.bio}</p>}
        </div>
      </section>

      <dl className="counters data" aria-label="Counters">
        <div>
          <dd>{posts}</dd>
          <dt>posts</dt>
        </div>
        <div>
          <dd>{followers}</dd>
          <dt>followers</dt>
        </div>
        <div>
          <dd>{user.followingCount ?? 0}</dd>
          <dt>following</dt>
        </div>
      </dl>

      <div className="profile-actions">
        {own ? (
          <>
            <Link to="/profile/edit" className="btn btn--ghost">
              Edit profile
            </Link>
            {auth.status === 'authenticated' && (
              <button type="button" className="btn btn--ghost" onClick={auth.logout}>
                Sign out
              </button>
            )}
          </>
        ) : (
          <FollowButton userId={id} initial={following} onChange={(f) => setFollowers((n) => Math.max(0, n + (f ? 1 : -1)))} />
        )}
      </div>

      {error && (
        <div className="error-line" role="alert">
          {error}
        </div>
      )}
      {!tiles && !error && <p className="data muted">Rewinding the film…</p>}
      {tiles && tiles.length === 0 && (
        <p className="data muted empty-grid">{own ? 'Tap + and add your first video.' : 'No posts yet.'}</p>
      )}
      {tiles && tiles.length > 0 && (
        <ul className="grid">
          {tiles.map((t) => {
            const label = thumbLabel(t.status);
            return (
              <li key={t.id}>
                <Link to={videoPath(t.id)} className="grid__cell" aria-label={t.title}>
                  <Poster className="grid__img" path={t.poster} alt="" />
                  {label && <span className="grid__label data">{label}</span>}
                </Link>
              </li>
            );
          })}
        </ul>
      )}
      {next && (
        <p className="more">
          <button type="button" className="btn btn--ghost" onClick={loadMore} disabled={more}>
            {more ? 'Loading…' : 'Show more'}
          </button>
        </p>
      )}
    </>
  );
}
