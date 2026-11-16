import { useCallback, useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { api, ApiError } from '../../shared/api/client';
import type { Description, Video } from '../../shared/api/types';
import { useMe } from '../../shared/lib/data';
import { describeState, formatTags, isVertical, shouldPollDescription } from '../../shared/lib/description';
import { failureMessage, fileSize, isInProgress, resolution, timecode } from '../../shared/lib/format';
import { relativeTime } from '../../shared/lib/relativeTime';
import { emptyLike, formatCount } from '../../shared/lib/likes';
import { likersPath, profilePath } from '../../shared/lib/urls';
import { useLikes } from '../../shared/lib/useLikes';
import { usePolling } from '../../shared/lib/usePolling';
import { Avatar } from '../../shared/ui/Avatar';
import { LikeButton } from '../../shared/ui/LikeButton';
import { LikeSurface } from '../../shared/ui/LikeSurface';
import { StatusMark } from '../../shared/ui/StatusMark';
import { Comments } from '../comments/Comments';
import { Player } from './Player';

export function WatchPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const me = useMe();
  const [video, setVideo] = useState<Video | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [description, setDescription] = useState<Description | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const { likes, load: loadLikes, set: setLike, toggle: toggleLike } = useLikes(
    (ids) => api.getVideoLikeInfo(ids),
    (vid, on) => api.setVideoLike(vid, on),
  );

  const load = useCallback(
    (signal?: AbortSignal) =>
      api.getVideo(id, signal).then(
        (v) => {
          setVideo(v);
          return v;
        },
        (e: unknown) => {
          if (!signal?.aborted) setError(e instanceof ApiError ? e.message : 'Could not load the video.');
          return null;
        },
      ),
    [id],
  );

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  usePolling(async (signal) => {
    const v = await load(signal);
    return !!v && isInProgress(v.status);
  }, !!video && isInProgress(video.status));

  const isOwner = !!video && !!me && video.ownerId === me.id;
  const videoStatus = video?.status ?? 'processing';
  const state = describeState(description?.status ?? null, videoStatus);
  // Someone else's video: the description is visible only in the done state, otherwise 404 — stay silent and do not poll.
  const pollDescription = isOwner && shouldPollDescription(state);

  useEffect(() => {
    if (video?.status !== 'ready') return;
    const controller = new AbortController();
    api.getDescription(id, controller.signal).then(
      (d) => d && setDescription(d),
      () => undefined,
    );
    return () => controller.abort();
  }, [video?.status, id]);

  usePolling(async (signal) => {
    try {
      const d = await api.getDescription(id, signal);
      if (d) setDescription(d);
    } catch {
      /* transient failure — will retry */
    }
    return true;
  }, pollDescription && video?.status === 'ready');

  useEffect(() => {
    if (video?.status === 'ready') loadLikes([id]);
  }, [video?.status, id, loadLikes]);

  const remove = async () => {
    setDeleting(true);
    setDeleteError(null);
    try {
      await api.deleteVideo(id);
      navigate(me ? profilePath(me.id) : '/', { replace: true });
    } catch (e) {
      setDeleteError(e instanceof ApiError ? e.message : 'Could not delete the video.');
      setDeleting(false);
    }
  };

  if (error) {
    return (
      <section className="notice">
        <h1>Not found</h1>
        <p>{error}</p>
        <Link to="/" className="btn btn--ghost">
          Back to feed
        </Link>
      </section>
    );
  }

  if (!video) return <p className="data muted">Fetching the reel…</p>;

  const vertical = isVertical(video.width, video.height);
  const showDescription = isOwner || state === 'done';

  return (
    <>
      <div className="watch-author">
        {video.ownerId && video.ownerUsername ? (
          <Link to={profilePath(video.ownerId)} className="post__author">
            <Avatar username={video.ownerUsername} size="sm" />
            <b>{video.ownerUsername}</b>
          </Link>
        ) : (
          <span />
        )}
        <span className="data muted">
          <StatusMark status={video.status} /> · {relativeTime(video.createdAt)}
        </span>
      </div>

      <LikeSurface onDoubleLike={() => video.status === 'ready' && setLike(video.id, true)}>
      {video.status === 'ready' && video.playback ? (
        <Player playback={video.playback} renditions={video.renditions ?? []} title={video.title} horizontal={!vertical} />
      ) : (
        <div className={vertical ? 'screen' : 'screen screen--wide'}>
          <p className="data" style={{ color: 'var(--paper)', textAlign: 'center', padding: 'var(--space-4)' }}>
            {video.status === 'failed'
              ? failureMessage(video.failureReason)
              : video.status === 'awaiting_upload'
                ? 'The file has not been uploaded yet.'
                : 'Processing: cutting qualities and segments. The page will update on its own.'}
          </p>
        </div>
      )}

      </LikeSurface>

      {video.status === 'ready' && (
        <div className="post__actions data">
          <LikeButton
            count={(likes[video.id] ?? emptyLike(video.id)).count}
            liked={likes[video.id]?.likedByMe ?? false}
            onToggle={() => toggleLike(video.id)}
            label={likes[video.id]?.likedByMe ? 'Unlike' : 'Like'}
          />
          <Link to={likersPath(video.id)}>
            {formatCount(likes[video.id]?.count ?? 0)} {(likes[video.id]?.count ?? 0) === 1 ? 'like' : 'likes'}
          </Link>
        </div>
      )}

      <h1 className="watch-title">{video.title}</h1>

      {showDescription && (
        <section className="about" aria-labelledby="about-h">
          <h2 id="about-h" className="section-label">
            What's in the video
          </h2>
          {state === 'done' && description ? (
            <>
              <p className="about__text">{description.text}</p>
              {description.tags.length > 0 && <p className="data about__tags">{formatTags(description.tags)}</p>}
            </>
          ) : state === 'working' ? (
            <p className="data muted" role="status">
              Analysing frames…
            </p>
          ) : state === 'failed' ? (
            <p className="data">The description could not be built. The video itself is unaffected.</p>
          ) : state === 'wait' ? (
            <p className="data muted">The description will appear after processing</p>
          ) : (
            <p className="data muted">There will be no description: the video was not processed.</p>
          )}
        </section>
      )}

      {video.status === 'ready' && <Comments videoId={video.id} />}

      <details className="passport">
        <summary className="section-label">Video details</summary>
        <dl className="specs">
          <dt>Duration</dt>
          <dd>{timecode(video.durationMs)}</dd>
          <dt>Frame</dt>
          <dd>{resolution(video.width, video.height)}</dd>
          <dt>Source</dt>
          <dd>{fileSize(video.sizeBytes)}</dd>
          <dt>Protocols</dt>
          <dd>{video.playback ? 'DASH · HLS (CMAF)' : '—'}</dd>
          <dt>Qualities</dt>
          <dd>
            {video.renditions?.length
              ? video.renditions.map((r) => (
                  <div key={r.name}>
                    {r.name.padEnd(6, ' ')} {r.width}×{r.height} · {r.kbps} kbit/s
                  </div>
                ))
              : '—'}
          </dd>
        </dl>
      </details>

      {isOwner && (
        <section className="danger-zone">
          {confirming ? (
            <>
              <p>Delete the video permanently? Comments and the description will be removed too.</p>
              {deleteError && (
                <div className="error-line" role="alert">
                  {deleteError}
                </div>
              )}
              <button type="button" className="btn" onClick={() => void remove()} disabled={deleting}>
                {deleting ? 'Deleting…' : 'Delete permanently'}
              </button>{' '}
              <button type="button" className="btn btn--ghost" onClick={() => setConfirming(false)} disabled={deleting}>
                Cancel
              </button>
            </>
          ) : (
            <button type="button" className="btn btn--ghost" onClick={() => setConfirming(true)}>
              Delete video
            </button>
          )}
        </section>
      )}
    </>
  );
}
