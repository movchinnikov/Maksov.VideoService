import type { DescriptionStatus, VideoStatus } from '../api/types';

export type DescriptionState = 'wait' | 'working' | 'done' | 'failed' | 'none';

/**
 * Which state of the “What's in the video” block to show.
 * wait — the video is not ready yet; working — analysing frames; none — the video failed, there will be no description.
 */
export function describeState(status: DescriptionStatus | null, videoStatus: VideoStatus): DescriptionState {
  if (videoStatus === 'failed') return 'none';
  if (videoStatus !== 'ready') return 'wait';
  if (status === null || status === 'queued' || status === 'processing') return 'working';
  return status;
}

/** Polling is needed until the description reaches a terminal state. */
export function shouldPollDescription(state: DescriptionState): boolean {
  return state === 'wait' || state === 'working';
}

export function isVertical(width: number | null, height: number | null): boolean {
  if (!width || !height) return true;
  return height >= width;
}

export function formatTags(tags: string[]): string {
  return tags.map((t) => t.trim()).filter(Boolean).join(' · ');
}
