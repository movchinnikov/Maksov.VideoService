import { NavLink, Outlet, useLocation } from 'react-router';
import { useAuth } from '../../app/auth';
import { useMe } from '../lib/data';
import { isOwnProfilePath, profilePath } from '../lib/urls';

export function Layout() {
  const auth = useAuth();
  const me = useMe();
  const { pathname } = useLocation();
  const authed = auth.status === 'authenticated';
  const profileActive = isOwnProfilePath(pathname, me?.id ?? null) || pathname.startsWith('/profile');

  return (
    <div className="frame">
      <header className="masthead">
        <NavLink to="/" className="masthead__brand">
          <b>Film</b>
        </NavLink>
      </header>
      <main>
        <Outlet />
      </main>
      {authed && (
        <nav className="tabbar" aria-label="Main navigation">
          <NavLink to="/" end className="tabbar__tab">
            Feed
          </NavLink>
          <NavLink to="/search" className="tabbar__tab">
            Search
          </NavLink>
          <NavLink to="/upload" className="tabbar__add">
            + Add
          </NavLink>
          <NavLink
            to={me ? profilePath(me.id) : '/profile'}
            className={`tabbar__tab${profileActive ? ' active' : ''}`}
            aria-current={profileActive ? 'page' : undefined}
          >
            Profile
          </NavLink>
        </nav>
      )}
    </div>
  );
}
