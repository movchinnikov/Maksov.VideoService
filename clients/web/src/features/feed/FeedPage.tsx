import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router';
import { api, ApiError } from '../../shared/api/client';
import type { Description, FeedItem } from '../../shared/api/types';
import { forgetDescription, getDescriptionCached, useInView, useMe } from '../../shared/lib/data';
import { isVertical } from '../../shared/lib/description';
import { mergePages } from '../../shared/lib/pages';
import { relativeTime } from '../../shared/lib/relativeTime';
import { commentsPath, profilePath, videoPath } from '../../shared/lib/urls';
import { Avatar } from '../../shared/ui/Avatar';
import { Poster } from '../../shared/ui/Poster';
import { Player } from '../watch/Player';

type State =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'loaded'; items: FeedItem[]; nextCursor: string | null; loadingMore: boolean };

/** Feed returns ids; cards are fetched in a second call (order preserved, missing ids skipped). */
async function loadFeedPage(cursor?: string, signal?: AbortSignal): Promise<{ items: FeedItem[]; nextCursor: string | null }> {
  const page = await api.getFeed(cursor, signal);
  const cards = await api.getCards(page.ids, signal);
  return { items: cards.items, nextCursor: page.nextCursor };
}

export function FeedPage() {
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [activeId, setActiveId] = useState<string | null>(null);
  const me = useMe();

  const load = useCallback((signal?: AbortSignal) => {
    loadFeedPage(undefined, signal).then(
      (page) => setState({ kind: 'loaded', items: page.items, nextCursor: page.nextCursor, loadingMore: false }),
      (e: unknown) => {
        if (!signal?.aborted) setState({ kind: 'error', message: e instanceof ApiError ? e.message : 'Could not reach the server.' });
      },
    );
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal);
    return () => controller.abort();
  }, [load]);

  const loadMore = () => {
    if (state.kind !== 'loaded' || !state.nextCursor || state.loadingMore) return;
    const { items, nextCursor } = state;
    setState({ ...state, loadingMore: true });
    loadFeedPage(nextCursor).then(
      (page) => setState({ kind: 'loaded', items: mergePages(items, page.items), nextCursor: page.nextCursor, loadingMore: false }),
      () => setState({ kind: 'loaded', items, nextCursor, loadingMore: false }),
    );
  };

  const remove = async (id: string) => {
    if (state.kind !== 'loaded') return;
    try {
      await api.deleteVideo(id);
      forgetDescription(id);
      setState({ ...state, items: state.items.filter((x) => x.id !== id) });
    } catch {
      /* the card stays in place */
    }
  };

  return (
    <>
      <h1 className="visually-hidden">Feed</h1>
      {state.kind === 'loading' && <p className="data muted">Rewinding the film…</p>}
      {state.kind === 'error' && (
        <div className="error-line" role="alert">
          {state.message}{' '}
          <button
            type="button"
            className="btn btn--ghost"
            onClick={() => {
              setState({ kind: 'loading' });
              load();
            }}
          >
            Retry
          </button>
        </div>
      )}
      {state.kind === 'loaded' && state.items.length === 0 && (
        <section className="notice">
          <h2>Your feed is empty</h2>
          <p>Follow someone — find people in the “Search” tab.</p>
          <Link to="/search" className="btn">
            Find people
          </Link>
        </section>
      )}
      {state.kind === 'loaded' &&
        state.items.map((item) => (
          <FeedCard
            key={item.id}
            item={item}
            own={me?.id === item.ownerId}
            active={activeId === item.id}
            onPlay={() => setActiveId(item.id)}
            onDelete={() => void remove(item.id)}
          />
        ))}
      {state.kind === 'loaded' && state.nextCursor && (
        <p className="more">
          <button type="button" className="btn btn--ghost" onClick={loadMore} disabled={state.loadingMore}>
            {state.loadingMore ? 'Loading…' : 'Show more'}
          </button>
        </p>
      )}
    </>
  );
}

function FeedCard({
  item,
  own,
  active,
  onPlay,
  onDelete,
}: {
  item: FeedItem;
  own: boolean;
  active: boolean;
  onPlay: () => void;
  onDelete: () => void;
}) {
  const [ref, seen] = useInView<HTMLElement>();
  const [description, setDescription] = useState<Description | null>(null);
  const [confirming, setConfirming] = useState(false);

  useEffect(() => {
    if (!seen) return;
    let alive = true;
    getDescriptionCached(item.id).then(
      (d) => alive && setDescription(d),
      () => undefined,
    );
    return () => {
      alive = false;
    };
  }, [seen, item.id]);

  const horizontal = !isVertical(item.width, item.height);

  return (
    <article className="post" ref={ref}>
      <header className="post__head">
        <Link to={profilePath(item.ownerId)} className="post__author">
          <Avatar username={item.ownerUsername} size="sm" />
          <b>{item.ownerUsername}</b>
        </Link>
        <time className="data muted" dateTime={item.createdAt}>
          {relativeTime(item.createdAt)}
        </time>
      </header>
      {active ? (
        <Player playback={item.playback} renditions={[]} title={item.title} horizontal={horizontal} autoPlay />
      ) : (
        <button
          type="button"
          className={horizontal ? 'post__media post__media--wide' : 'post__media'}
          onClick={onPlay}
          aria-label={`Watch “${item.title}”`}
        >
          <Poster className="post__poster" path={item.playback.poster} alt="" />
          <span className="post__play data">watch</span>
        </button>
      )}
      <div className="post__actions data">
        <Link to={commentsPath(item.id)}>Comments</Link>
        <Link to={videoPath(item.id)}>Open</Link>
        {own &&
          (confirming ? (
            <span>
              Delete permanently?{' '}
              <button type="button" className="linklike" onClick={onDelete}>
                Yes
              </button>{' '}
              <button type="button" className="linklike" onClick={() => setConfirming(false)}>
                No
              </button>
            </span>
          ) : (
            <button type="button" className="linklike" onClick={() => setConfirming(true)}>
              Delete
            </button>
          ))}
      </div>
      {description?.status === 'done' && description.text && (
        <p className="post__caption">
          <b>{item.ownerUsername}</b> {description.text}
        </p>
      )}
    </article>
  );
}
