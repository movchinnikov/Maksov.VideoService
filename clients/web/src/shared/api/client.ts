import { getAccessToken } from '../../app/auth';
import { chunk, LIKE_BATCH_MAX } from '../lib/likes';
import type {
  CreateUploadRequest,
  CreateUploadResponse,
  Comment,
  CommentPage,
  Description,
  FeedIdsPage,
  FeedItem,
  FeedPage,
  LikeInfo,
  LikerPage,
  FollowPage,
  Recommendation,
  UserStats,
  UserCard,
  UserSummary,
  ProblemDetails,
  Profile,
  UpdateProfileRequest,
  Video,
  VideoPage,
} from './types';

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails | null;

  constructor(status: number, problem: ProblemDetails | null) {
    super(describeProblem(status, problem));
    this.status = status;
    this.problem = problem;
  }
}

/** Problem Details → short human-readable message. */
export function describeProblem(status: number, problem: ProblemDetails | null): string {
  const fieldError = problem?.errors ? Object.values(problem.errors).flat()[0] : undefined;
  if (fieldError) return fieldError;
  if (problem?.title && status !== 500) return problem.title;
  switch (status) {
    case 401:
      return 'Session expired. Please sign in again.';
    case 404:
      return 'No such video — or it is not yours.';
    case 429:
      return 'Too many uploads in a row. Wait a minute.';
    default:
      return status >= 500 ? 'The server did not respond properly. Please try again in a minute.' : `Request failed (${status}).`;
  }
}

async function requestVoid(path: string, init: RequestInit = {}): Promise<void> {
  const token = await getAccessToken();
  const headers = new Headers(init.headers);
  if (token) headers.set('Authorization', `Bearer ${token}`);
  if (init.body) headers.set('Content-Type', 'application/json');
  const response = await fetch(path, { ...init, headers });
  if (!response.ok) {
    const problem = response.headers.get('content-type')?.includes('json')
      ? ((await response.json()) as ProblemDetails)
      : null;
    throw new ApiError(response.status, problem);
  }
}

function qs(params: Record<string, string | number | undefined>): string {
  const parts = Object.entries(params)
    .filter(([, v]) => v !== undefined && v !== '')
    .map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`);
  return parts.length ? `?${parts.join('&')}` : '';
}

async function request<T>(path: string, init: RequestInit = {}, signal?: AbortSignal): Promise<T> {
  const token = await getAccessToken();
  const headers = new Headers(init.headers);
  headers.set('Accept', 'application/json');
  if (token) headers.set('Authorization', `Bearer ${token}`);
  if (init.body) headers.set('Content-Type', 'application/json');

  const response = await fetch(path, { ...init, headers, signal });
  if (!response.ok) {
    const problem = response.headers.get('content-type')?.includes('json')
      ? ((await response.json()) as ProblemDetails)
      : null;
    throw new ApiError(response.status, problem);
  }
  return (await response.json()) as T;
}

/** Batch like info; the server accepts at most 50 ids per call, so larger lists are chunked. */
async function fetchLikeInfo(kind: 'videos' | 'comments', ids: string[], signal?: AbortSignal): Promise<LikeInfo[]> {
  const pages = await Promise.all(
    chunk(ids, LIKE_BATCH_MAX).map((part) =>
      request<{ items: LikeInfo[] }>(`/api/likes/${kind}${qs({ ids: part.join(',') })}`, {}, signal),
    ),
  );
  return pages.flatMap((p) => p.items);
}

export const api = {
  listVideos: (cursor?: string, signal?: AbortSignal) =>
    request<VideoPage>(`/api/videos${cursor ? `?cursor=${encodeURIComponent(cursor)}` : ''}`, {}, signal),
  getVideo: (id: string, signal?: AbortSignal) => request<Video>(`/api/videos/${encodeURIComponent(id)}`, {}, signal),
  createUpload: (body: CreateUploadRequest) =>
    request<CreateUploadResponse>('/api/videos', { method: 'POST', body: JSON.stringify(body) }),
  /** 404 — the analysis has not been created yet: this is a “waiting” state, not an error. */
  getDescription: async (videoId: string, signal?: AbortSignal): Promise<Description | null> => {
    try {
      return await request<Description>(`/api/videos/${encodeURIComponent(videoId)}/description`, {}, signal);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },
  getMe: (signal?: AbortSignal) => request<Profile>('/api/users/me', {}, signal),
  updateMe: (body: UpdateProfileRequest) =>
    request<Profile>('/api/users/me', { method: 'PUT', body: JSON.stringify(body) }),
  getFeed: (cursor?: string, signal?: AbortSignal) =>
    request<FeedIdsPage>(`/api/feed${qs({ cursor, limit: 10 })}`, {}, signal),
  getCards: (ids: string[], signal?: AbortSignal) =>
    ids.length === 0
      ? Promise.resolve({ items: [] as FeedItem[] })
      : request<{ items: FeedItem[] }>(`/api/videos/cards${qs({ ids: ids.join(',') })}`, {}, signal),
  getStats: (id: string, signal?: AbortSignal) =>
    request<UserStats>(`/api/friends/${encodeURIComponent(id)}/stats`, {}, signal),
  getFollowingStatus: (ids: string[], signal?: AbortSignal) =>
    ids.length === 0
      ? Promise.resolve({ following: [] as string[], followedBy: [] as string[] })
      : request<{ following: string[]; followedBy?: string[] }>(`/api/friends/status${qs({ ids: ids.join(',') })}`, {}, signal),
  getFollowList: (id: string, kind: 'followers' | 'following', cursor?: string, signal?: AbortSignal) =>
    request<FollowPage>(`/api/friends/${encodeURIComponent(id)}/${kind}${qs({ cursor, limit: 20 })}`, {}, signal),
  setVideoLike: (videoId: string, on: boolean) =>
    requestVoid(`/api/videos/${encodeURIComponent(videoId)}/like`, { method: on ? 'PUT' : 'DELETE' }),
  setCommentLike: (videoId: string, commentId: string, on: boolean) =>
    requestVoid(`/api/videos/${encodeURIComponent(videoId)}/comments/${encodeURIComponent(commentId)}/like`, {
      method: on ? 'PUT' : 'DELETE',
    }),
  getVideoLikeInfo: (ids: string[], signal?: AbortSignal) => fetchLikeInfo('videos', ids, signal),
  getCommentLikeInfo: (ids: string[], signal?: AbortSignal) => fetchLikeInfo('comments', ids, signal),
  getLikers: (videoId: string, cursor?: string, signal?: AbortSignal) =>
    request<LikerPage>(`/api/videos/${encodeURIComponent(videoId)}/likes${qs({ cursor, limit: 20 })}`, {}, signal),
  getRecommendations: (signal?: AbortSignal) =>
    request<{ items: Recommendation[] }>(`/api/friends/recommendations${qs({ limit: 10 })}`, {}, signal),
  getUsersByIds: (ids: string[], signal?: AbortSignal) =>
    ids.length === 0
      ? Promise.resolve({ items: [] as UserSummary[] })
      : request<{ items: UserSummary[] }>(`/api/users${qs({ ids: ids.join(',') })}`, {}, signal),
  getUserVideos: (id: string, cursor?: string, signal?: AbortSignal) =>
    request<FeedPage>(`/api/users/${encodeURIComponent(id)}/videos${qs({ cursor, limit: 30 })}`, {}, signal),
  deleteVideo: (id: string) => requestVoid(`/api/videos/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  getComments: (videoId: string, cursor?: string, signal?: AbortSignal) =>
    request<CommentPage>(`/api/videos/${encodeURIComponent(videoId)}/comments${qs({ cursor, limit: 20 })}`, {}, signal),
  addComment: (videoId: string, text: string) =>
    request<Comment>(`/api/videos/${encodeURIComponent(videoId)}/comments`, {
      method: 'POST',
      body: JSON.stringify({ text }),
    }),
  deleteComment: (videoId: string, commentId: string) =>
    requestVoid(`/api/videos/${encodeURIComponent(videoId)}/comments/${encodeURIComponent(commentId)}`, {
      method: 'DELETE',
    }),
  getUser: (id: string, signal?: AbortSignal) => request<UserCard>(`/api/users/${encodeURIComponent(id)}`, {}, signal),
  searchUsers: (query: string, signal?: AbortSignal) =>
    request<{ items: UserSummary[] }>(`/api/users${qs({ query, limit: 20 })}`, {}, signal),
  follow: (id: string) => requestVoid(`/api/users/${encodeURIComponent(id)}/follow`, { method: 'PUT' }),
  unfollow: (id: string) => requestVoid(`/api/users/${encodeURIComponent(id)}/follow`, { method: 'DELETE' }),
  completeUpload: (id: string) => request<Video>(`/api/videos/${encodeURIComponent(id)}/complete`, { method: 'POST' }),
};

/** Protected binary resource (poster) → object URL. Release via URL.revokeObjectURL. */
export async function fetchObjectUrl(path: string, signal?: AbortSignal): Promise<string> {
  const token = await getAccessToken();
  const response = await fetch(path, { headers: token ? { Authorization: `Bearer ${token}` } : {}, signal });
  if (!response.ok) throw new ApiError(response.status, null);
  return URL.createObjectURL(await response.blob());
}

