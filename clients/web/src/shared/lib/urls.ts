export const profilePath = (id: string) => `/u/${encodeURIComponent(id)}`;
export const followersPath = (id: string) => `${profilePath(id)}/followers`;
export const followingPath = (id: string) => `${profilePath(id)}/following`;
export const likersPath = (id: string) => `/v/${encodeURIComponent(id)}/likes`;
export const videoPath = (id: string) => `/videos/${encodeURIComponent(id)}`;
export const commentsPath = (id: string) => `${videoPath(id)}#comments`;

export function isOwnProfilePath(pathname: string, myId: string | null): boolean {
  return !!myId && pathname === profilePath(myId);
}
