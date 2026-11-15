import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link } from 'react-router';
import { api, ApiError } from '../../shared/api/client';
import { invalidateMe } from '../../shared/lib/data';
import { profilePath } from '../../shared/lib/urls';
import type { Profile } from '../../shared/api/types';

const NAME_MAX = 60;
const BIO_MAX = 300;

export function EditProfilePage() {
  const [profile, setProfile] = useState<Profile | null>(null);
  const [displayName, setDisplayName] = useState('');
  const [bio, setBio] = useState('');
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    api.getMe(controller.signal).then(
      (me) => {
        setProfile(me);
        setDisplayName(me.displayName);
        setBio(me.bio);
      },
      (e: unknown) => {
        if (!controller.signal.aborted) setLoadError(e instanceof ApiError ? e.message : 'Could not load the profile.');
      },
    );
    return () => controller.abort();
  }, []);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setSaved(false);
    setSaveError(null);
    try {
      const me = await api.updateMe({ displayName: displayName.trim(), bio });
      setProfile(me);
      setDisplayName(me.displayName);
      setBio(me.bio);
      setSaved(true);
      invalidateMe();
    } catch (err) {
      setSaveError(err instanceof ApiError ? err.message : 'Could not save. Please try again.');
    } finally {
      setSaving(false);
    }
  }

  if (loadError) {
    return (
      <section className="notice">
        <h1>Profile unavailable</h1>
        <p>{loadError}</p>
      </section>
    );
  }
  if (!profile) return <p className="data muted">Fetching the profile card…</p>;

  const nameEmpty = displayName.trim().length === 0;

  return (
    <>
      <div className="page-head">
        <h1>Edit profile</h1>
        <p className="data muted">
          <Link to={profilePath(profile.id)}>@{profile.username}</Link>
        </p>
      </div>
      <form className="desk profile-form" onSubmit={submit} noValidate>
        <div className="field">
          <label htmlFor="displayName">Display name</label>
          <input
            id="displayName"
            type="text"
            value={displayName}
            maxLength={NAME_MAX}
            onChange={(e) => {
              setDisplayName(e.target.value);
              setSaved(false);
            }}
            aria-invalid={nameEmpty}
          />
          <span className="data muted">{displayName.length} / {NAME_MAX}</span>
        </div>
        <div className="field">
          <label htmlFor="bio">About</label>
          <textarea
            id="bio"
            rows={5}
            value={bio}
            maxLength={BIO_MAX}
            onChange={(e) => {
              setBio(e.target.value);
              setSaved(false);
            }}
          />
          <span className="data muted">{bio.length} / {BIO_MAX}</span>
        </div>
        {saveError && (
          <div className="error-line" role="alert">
            {saveError}
          </div>
        )}
        <button type="submit" className="btn" disabled={saving || nameEmpty}>
          {saving ? 'Saving…' : 'Save'}
        </button>
        {saved && (
          <span className="data" role="status" style={{ marginLeft: 'var(--space-3)' }}>
            Saved
          </span>
        )}
      </form>
    </>
  );
}
