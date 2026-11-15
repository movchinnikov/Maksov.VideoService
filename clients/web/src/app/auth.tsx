import { InMemoryWebStorage, UserManager, WebStorageStateStore, type User } from 'oidc-client-ts';
import { createContext, use, useEffect, useMemo, useState, type ReactNode } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router';

const authority = import.meta.env.VITE_OIDC_AUTHORITY ?? 'http://localhost:8080/realms/videoservice';
const clientId = import.meta.env.VITE_OIDC_CLIENT_ID ?? 'videoservice-web';

/**
 * OIDC Authorization Code + PKCE against Keycloak. Tokens are kept in memory only (not localStorage).
 * After a page reload the session is restored silently (prompt=none) via the Keycloak cookie.
 * External SSO providers (Google, Microsoft) are shown on the Keycloak login page.
 */
export const userManager = new UserManager({
  authority,
  client_id: clientId,
  redirect_uri: `${window.location.origin}/auth/callback`,
  silent_redirect_uri: `${window.location.origin}/auth/silent`,
  post_logout_redirect_uri: window.location.origin,
  response_type: 'code',
  scope: 'openid profile email',
  userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
  stateStore: new WebStorageStateStore({ store: window.sessionStorage }),
  automaticSilentRenew: true,
});

export async function getAccessToken(): Promise<string | null> {
  const user = await userManager.getUser();
  if (user && !user.expired) {
    return user.access_token;
  }
  try {
    const renewed = await userManager.signinSilent();
    return renewed?.access_token ?? null;
  } catch {
    return null;
  }
}

type AuthState =
  | { status: 'loading' }
  | { status: 'anonymous'; login: () => void }
  | { status: 'authenticated'; user: User; logout: () => void };

const AuthContext = createContext<AuthState>({ status: 'loading' });

export const useAuth = () => use(AuthContext);

type ReturnState = { returnTo?: string };

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null | undefined>(undefined);
  const location = useLocation();
  const navigate = useNavigate();

  useEffect(() => {
    let cancelled = false;
    const path = window.location.pathname;

    async function init() {
      if (path === '/auth/silent') {
        await userManager.signinSilentCallback();
        return;
      }
      if (path === '/auth/callback') {
        try {
          const signedIn = await userManager.signinRedirectCallback();
          if (!cancelled) {
            setUser(signedIn);
            navigate((signedIn.state as ReturnState | undefined)?.returnTo ?? '/', { replace: true });
          }
        } catch {
          if (!cancelled) {
            setUser(null);
            navigate('/', { replace: true });
          }
        }
        return;
      }
      try {
        const restored = (await userManager.getUser()) ?? (await userManager.signinSilent());
        if (!cancelled) setUser(restored);
      } catch {
        if (!cancelled) setUser(null);
      }
    }

    void init();
    const onLoaded = (u: User) => setUser(u);
    const onUnloaded = () => setUser(null);
    userManager.events.addUserLoaded(onLoaded);
    userManager.events.addUserUnloaded(onUnloaded);
    return () => {
      cancelled = true;
      userManager.events.removeUserLoaded(onLoaded);
      userManager.events.removeUserUnloaded(onUnloaded);
    };
    // Initialize once per app load.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const value = useMemo<AuthState>(() => {
    if (user === undefined) return { status: 'loading' };
    if (user === null) {
      return {
        status: 'anonymous',
        login: () => void userManager.signinRedirect({ state: { returnTo: location.pathname } satisfies ReturnState }),
      };
    }
    return { status: 'authenticated', user, logout: () => void userManager.signoutRedirect() };
  }, [user, location.pathname]);

  if (window.location.pathname === '/auth/silent') {
    return null;
  }

  return <AuthContext value={value}>{children}</AuthContext>;
}

export function RequireAuth() {
  const auth = useAuth();
  if (auth.status === 'loading') {
    return <p className="notice data muted">Checking session…</p>;
  }
  if (auth.status === 'anonymous') {
    return (
      <section className="notice">
        <h1>Sign in</h1>
        <p>Film stores your videos and shows them only to you. Sign in to upload your first one.</p>
        <button type="button" className="btn" onClick={auth.login}>
          Sign in
        </button>
        <p className="data muted">You can sign in with Google or Microsoft if an administrator has enabled them.</p>
      </section>
    );
  }
  return <Outlet />;
}

