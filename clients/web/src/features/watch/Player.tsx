import { useEffect, useRef, useState } from 'react';
import type shakaNs from 'shaka-player';
import { getAccessToken } from '../../app/auth';
import type { Playback, Rendition } from '../../shared/api/types';

type Protocol = 'dash' | 'hls';
type Shaka = typeof shakaNs;
type ShakaPlayer = InstanceType<Shaka['Player']>;

interface QualityOption {
  height: number;
  width: number;
  bandwidth: number;
}

/**
 * Shaka-based player: DASH and HLS from the same CMAF segments, ABR by default, manual quality selection.
 * Shaka is loaded lazily (separate chunk); the token is added by a request filter to every manifest/segment request.
 */
export function Player({
  playback,
  renditions,
  title,
  horizontal = false,
  autoPlay = false,
}: {
  playback: Playback;
  renditions: Rendition[];
  title: string;
  horizontal?: boolean;
  autoPlay?: boolean;
}) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const playerRef = useRef<ShakaPlayer | null>(null);
  const [protocol, setProtocol] = useState<Protocol>('dash');
  const [qualities, setQualities] = useState<QualityOption[]>([]);
  const [quality, setQuality] = useState<'auto' | number>('auto');
  const [current, setCurrent] = useState<string>('');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    let disposed = false;
    let player: ShakaPlayer | null = null;

    (async () => {
      const { default: shaka } = await import('shaka-player');
      if (disposed) return;
      shaka.polyfill.installAll();
      if (!shaka.Player.isBrowserSupported()) {
        setError('The browser does not support adaptive video (MSE).');
        return;
      }

      player = new shaka.Player();
      playerRef.current = player;
      await player.attach(video);
      player.getNetworkingEngine()?.registerRequestFilter(async (_type, request) => {
        const token = await getAccessToken();
        if (token) request.headers['Authorization'] = `Bearer ${token}`;
      });

      const updateCurrent = () => {
        const active = player?.getVideoTracks().find((t) => t.active);
        if (active?.height) setCurrent(`${active.height}p · ${Math.round(active.bandwidth / 1000)} kbit/s`);
      };
      player.addEventListener('adaptation', updateCurrent);
      player.addEventListener('variantchanged', updateCurrent);
      player.addEventListener('error', () => setError('Could not play the stream.'));

      try {
        const position = video.currentTime;
        await player.load(protocol === 'dash' ? playback.dash : playback.hls, position > 0 ? position : undefined);
        if (disposed) return;
        const tracks = player
          .getVideoTracks()
          .filter((t): t is typeof t & { height: number; width: number } => t.height != null && t.width != null)
          .map((t) => ({ height: t.height, width: t.width, bandwidth: t.bandwidth }))
          .sort((a, b) => b.height - a.height);
        setQualities(dedupeByHeight(tracks));
        setQuality('auto');
        setError(null);
        updateCurrent();
        if (autoPlay) void video.play().catch(() => undefined);
      } catch {
        if (!disposed) setError('Could not load the manifest.');
      }
    })();

    return () => {
      disposed = true;
      playerRef.current = null;
      void player?.destroy();
    };
  }, [protocol, playback.dash, playback.hls, autoPlay]);

  const chooseQuality = (value: string) => {
    const player = playerRef.current;
    if (!player) return;
    if (value === 'auto') {
      player.configure({ abr: { enabled: true } });
      setQuality('auto');
      return;
    }
    const height = Number(value);
    const track = player.getVideoTracks().find((t) => t.height === height);
    if (!track) return;
    player.configure({ abr: { enabled: false } });
    player.selectVideoTrack(track, true);
    setQuality(height);
  };

  return (
    <div>
      <div className={horizontal ? 'screen screen--wide' : 'screen'}>
        <video ref={videoRef} controls playsInline aria-label={`Video “${title}”`} />
      </div>
      <div className="controls">
        <div className="segmented" role="group" aria-label="Protocol">
          {(['dash', 'hls'] as const).map((p) => (
            <button key={p} type="button" aria-pressed={protocol === p} onClick={() => setProtocol(p)}>
              {p.toUpperCase()}
            </button>
          ))}
        </div>
        <label className="data">
          <span className="visually-hidden">Quality</span>
          <select className="select" value={String(quality)} onChange={(e) => chooseQuality(e.target.value)}>
            <option value="auto">Auto (ABR)</option>
            {(qualities.length ? qualities : renditions.map((r) => ({ height: r.height, width: r.width, bandwidth: r.kbps * 1000 }))).map((q) => (
              <option key={q.height} value={q.height}>
                {Math.min(q.width, q.height)}p
              </option>
            ))}
          </select>
        </label>
        <span className="data muted" aria-live="polite">
          {current && `now: ${current}`}
        </span>
      </div>
      {error && (
        <div className="error-line" role="alert">
          {error}
        </div>
      )}
    </div>
  );
}

function dedupeByHeight(tracks: QualityOption[]): QualityOption[] {
  const seen = new Set<number>();
  return tracks.filter((t) => (seen.has(t.height) ? false : (seen.add(t.height), true)));
}

