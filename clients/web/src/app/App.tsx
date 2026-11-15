import { BrowserRouter, Navigate, Route, Routes } from 'react-router';
import { FeedPage } from '../features/feed/FeedPage';
import { FollowListPage } from '../features/profile/FollowListPage';
import { EditProfilePage } from '../features/profile/EditProfilePage';
import { ProfilePage, ProfileRedirect } from '../features/profile/ProfilePage';
import { SearchPage } from '../features/search/SearchPage';
import { UploadPage } from '../features/upload/UploadPage';
import { WatchPage } from '../features/watch/WatchPage';
import { Layout } from '../shared/ui/Layout';
import { AuthProvider, RequireAuth } from './auth';

export function App() {
  return (
    <BrowserRouter>
      <AuthProvider>
        <Routes>
          <Route element={<Layout />}>
            <Route element={<RequireAuth />}>
              <Route index element={<FeedPage />} />
              <Route path="upload" element={<UploadPage />} />
              <Route path="search" element={<SearchPage />} />
              <Route path="u/:id" element={<ProfilePage />} />
              <Route path="u/:id/followers" element={<FollowListPage kind="followers" />} />
              <Route path="u/:id/following" element={<FollowListPage kind="following" />} />
              <Route path="profile" element={<ProfileRedirect />} />
              <Route path="profile/edit" element={<EditProfilePage />} />
              <Route path="videos/:id" element={<WatchPage />} />
            </Route>
          </Route>
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  );
}

