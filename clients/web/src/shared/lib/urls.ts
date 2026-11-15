export const profilePath = (id: string) => `/u/${encodeURIComponent(id)}`;
export const videoPath = (id: string) => `/videos/${encodeURIComponent(id)}`;
export const commentsPath = (id: string) => `${videoPath(id)}#comments`;

export function isOwnProfilePath(pathname: string, myId: string | null): boolean {
  return !!myId && pathname === profilePath(myId);
}
