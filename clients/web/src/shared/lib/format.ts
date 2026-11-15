import type { VideoStatus } from '../api/types';

/** 83_456 ms → “01:23”; ≥ 1 hour → “1:02:03”. Timecode is monospaced, like on an editing desk. */
export function timecode(ms: number | null | undefined): string {
  if (ms == null || ms < 0) return '--:--';
  const total = Math.round(ms / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const mm = String(m).padStart(2, '0');
  const ss = String(s).padStart(2, '0');
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}

export function fileSize(bytes: number | null | undefined): string {
  if (bytes == null) return '—';
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(unit === 0 || value >= 100 ? 0 : 1)} ${units[unit]}`;
}

export function resolution(width: number | null, height: number | null): string {
  return width && height ? `${width}×${height}` : '—';
}

const dateFormat = new Intl.DateTimeFormat('en-US', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' });

export function shortDate(iso: string): string {
  return dateFormat.format(new Date(iso));
}

export const statusLabel: Record<VideoStatus, string> = {
  awaiting_upload: 'awaiting file',
  uploaded: 'queued',
  processing: 'editing',
  ready: 'ready',
  failed: 'rejected',
};

export function statusTone(status: VideoStatus): 'ready' | 'failed' | 'work' {
  if (status === 'ready') return 'ready';
  if (status === 'failed') return 'failed';
  return 'work';
}

export function isInProgress(status: VideoStatus): boolean {
  return status === 'uploaded' || status === 'processing';
}

const failureText: Record<string, string> = {
  unsupported_format: 'Format not supported. MP4, MOV, WebM, MKV will work.',
  no_video_stream: 'The file has no video track.',
  duration_exceeded: 'The video is longer than 10 minutes.',
  resolution_exceeded: 'Resolution is above 4K.',
  invalid_media: 'The file is corrupted or is not a video.',
  file_too_large: 'The file exceeds the allowed size.',
  too_many_streams: 'The file has too many tracks.',
  transcode_error: 'Could not process the video. Please try uploading again.',
  max_attempts: 'Could not process the video. Please try uploading again.',
};

export function failureMessage(code: string | null): string {
  return (code && failureText[code]) ?? 'Processing failed.';
}

/** “Sunset_over_sea.final.mp4” → “Sunset over sea.final”. */
export function titleFromFileName(name: string): string {
  return name.replace(/\.[^.]+$/, '').replace(/[_]+/g, ' ').trim().slice(0, 200);
}

