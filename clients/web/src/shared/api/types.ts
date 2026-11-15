/** Types per contracts/openapi/api.yaml. Update together when the contract changes. */

export type VideoStatus = 'awaiting_upload' | 'uploaded' | 'processing' | 'ready' | 'failed';

export interface Rendition {
  name: string;
  width: number;
  height: number;
  kbps: number;
}

export interface Playback {
  dash: string;
  hls: string;
  poster: string;
}

export interface Video {
  id: string;
  title: string;
  status: VideoStatus;
  createdAt: string;
  updatedAt: string;
  sizeBytes: number | null;
  durationMs: number | null;
  width: number | null;
  height: number | null;
  renditions: Rendition[] | null;
  failureReason: string | null;
  playback: Playback | null;
  ownerId?: string;
  ownerUsername?: string;
}

export interface VideoPage {
  items: Video[];
  nextCursor: string | null;
}

export interface CreateUploadRequest {
  title: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
}

export interface CreateUploadResponse {
  videoId: string;
  uploadUrl: string;
  uploadMethod: 'PUT';
  uploadHeaders: Record<string, string>;
  expiresAt: string;
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  errors?: Record<string, string[]>;
}


export type DescriptionStatus = 'queued' | 'processing' | 'done' | 'failed';

export interface Description {
  videoId: string;
  status: DescriptionStatus;
  text: string | null;
  tags: string[];
  updatedAt: string;
}

export interface Profile {
  id: string;
  username: string;
  displayName: string;
  bio: string;
  createdAt: string;
}

export interface UpdateProfileRequest {
  displayName: string;
  bio: string;
}

export interface FeedItem {
  id: string;
  title: string;
  ownerId: string;
  ownerUsername: string;
  createdAt: string;
  durationMs: number;
  width: number;
  height: number;
  playback: Playback;
}

export interface FeedPage {
  items: FeedItem[];
  nextCursor: string | null;
}

export interface Comment {
  id: string;
  videoId: string;
  authorId: string;
  authorUsername: string;
  text: string;
  createdAt: string;
  canDelete: boolean;
}

export interface CommentPage {
  items: Comment[];
  nextCursor: string | null;
  total: number;
}

export interface UserSummary {
  id: string;
  username: string;
  displayName: string;
  bio: string;
}

export interface UserCard {
  id: string;
  username: string;
  displayName: string;
  bio: string;
}

export interface FeedIdsPage {
  ids: string[];
  nextCursor: string | null;
}

export interface UserStats {
  followersCount: number;
  followingCount: number;
  isFollowing: boolean;
  followsYou: boolean;
  isMutual: boolean;
}

export interface FollowListItem {
  userId: string;
  followedAt: string;
  isFollowing: boolean;
  followsYou: boolean;
  isMutual: boolean;
}

export interface FollowPage {
  items: FollowListItem[];
  nextCursor: string | null;
  total: number;
}

export interface Recommendation {
  userId: string;
  mutualFriends: number;
  similarity: number;
}
