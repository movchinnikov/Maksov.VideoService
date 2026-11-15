import { useEffect, useState } from 'react';
import type { KeyboardEvent } from 'react';
import { Link } from 'react-router';
import { api, ApiError } from '../../shared/api/client';
import type { Comment } from '../../shared/api/types';
import { mergePages } from '../../shared/lib/pages';
import { relativeTime } from '../../shared/lib/relativeTime';
import { checkComment, COMMENT_MAX } from '../../shared/lib/social';
import { profilePath } from '../../shared/lib/urls';

export function Comments({ videoId }: { videoId: string }) {
  const [items, setItems] = useState<Comment[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [total, setTotal] = useState(0);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [text, setText] = useState('');
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [more, setMore] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    api.getComments(videoId, undefined, controller.signal).then(
      (p) => {
        setItems(p.items);
        setNext(p.nextCursor);
        setTotal(p.total);
      },
      (e: unknown) => {
        if (!controller.signal.aborted) setLoadError(e instanceof ApiError ? e.message : 'Comments failed to load.');
      },
    );
    return () => controller.abort();
  }, [videoId]);

  const check = checkComment(text);

  const send = async () => {
    if (check !== 'ok' || sending) return;
    setSending(true);
    setSendError(null);
    try {
      const c = await api.addComment(videoId, text.trim());
      setItems((prev) => [c, ...(prev ?? [])]);
      setTotal((n) => n + 1);
      setText('');
    } catch (e) {
      setSendError(e instanceof ApiError ? e.message : 'Could not send. Please try again.');
    } finally {
      setSending(false);
    }
  };

  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      void send();
    }
  };

  const remove = async (c: Comment) => {
    try {
      await api.deleteComment(videoId, c.id);
      setItems((prev) => (prev ?? []).filter((x) => x.id !== c.id));
      setTotal((n) => Math.max(0, n - 1));
    } catch {
      setSendError('Could not delete the comment.');
    }
  };

  const loadMore = () => {
    if (!next || more) return;
    setMore(true);
    api.getComments(videoId, next).then(
      (p) => {
        setItems((prev) => mergePages(prev ?? [], p.items));
        setNext(p.nextCursor);
        setTotal(p.total);
        setMore(false);
      },
      () => setMore(false),
    );
  };

  return (
    <section id="comments" className="comments" aria-labelledby="comments-h">
      <h2 id="comments-h" className="section-label">
        Comments{items ? ` · ${total}` : ''}
      </h2>

      <div className="field">
        <label htmlFor="comment-text">Add a comment</label>
        <textarea
          id="comment-text"
          rows={3}
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={onKey}
          aria-invalid={check === 'too_long'}
        />
        <span className={check === 'too_long' ? 'data' : 'data muted'}>
          {text.length} / {COMMENT_MAX}
        </span>
      </div>
      {sendError && (
        <div className="error-line" role="alert">
          {sendError}
        </div>
      )}
      <button type="button" className="btn" onClick={() => void send()} disabled={check !== 'ok' || sending}>
        {sending ? 'Sending…' : 'Send'}
      </button>

      {loadError && (
        <div className="error-line" role="alert">
          {loadError}
        </div>
      )}
      {!items && !loadError && <p className="data muted">Loading comments…</p>}
      {items && items.length === 0 && <p className="data muted">No comments yet.</p>}
      {items && items.length > 0 && (
        <ul className="comment-list">
          {items.map((c) => (
            <li key={c.id} className="comment">
              <p className="comment__meta data">
                <Link to={profilePath(c.authorId)}>
                  <b>{c.authorUsername}</b>
                </Link>{' '}
                <span className="muted">{relativeTime(c.createdAt)}</span>
                {c.canDelete && (
                  <button type="button" className="linklike" onClick={() => void remove(c)}>
                    Delete
                  </button>
                )}
              </p>
              <p className="comment__text">{c.text}</p>
            </li>
          ))}
        </ul>
      )}
      {next && (
        <p className="more">
          <button type="button" className="btn btn--ghost" onClick={loadMore} disabled={more}>
            {more ? 'Loading…' : 'Show more'}
          </button>
        </p>
      )}
    </section>
  );
}
