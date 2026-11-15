# 0003. Keycloak as the identity provider, external SSO via brokering

- **Status:** Accepted
- **Date:** 2026-11-15
- **Lesson / feature:** lesson-01

## Context
Sign-in/registration is needed, as well as sign-in via Google and Microsoft. Local only.

## Decision
- **Keycloak** (realm `videoservice`, imported from `deploy/keycloak/realm-videoservice.json`).
- Web is the public client `videoservice-web`, Authorization Code + **PKCE S256**, implicit and password grants disabled, exact redirect URIs.
- The API is a resource server: RS256 signature validation, `iss`, `aud=videoservice-api` (audience mapper), `exp`. Resource owner is `sub`.
- **Google and Microsoft** are connected in Keycloak as Identity Providers (`make idps`, secrets from `.env`). The application does not know about them — one token format.
- Docker network: the browser sees `localhost:8080`, the API sees `keycloak:8080`. `KC_HOSTNAME=http://localhost:8080` + `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`; the API fetches discovery via the internal address, while the issuer is validated against the public one.
- Tokens in the browser are kept in memory; after a reload — silent session restore (`prompt=none`).
- The `videoservice-cli` client with password grant is **only for `make demo`** on the local stand.

> Extended by [ADR 0005](0005-phone-login-keycloak.md): sign-in and registration by phone.

## Alternatives considered
| Option | Why rejected |
| --- | --- |
| ASP.NET Core Identity in the API | Our own IdP: registration, recovery, MFA, brokering — all by hand |
| Direct Google/Microsoft OAuth in the app | Different token formats, no unified `sub`, no own users |
| Duende IdentityServer | Commercial license |
| Authentik/Zitadel | Good alternatives, but Keycloak is the de facto standard in .NET teams and is in the requirements |

## Consequences
- (+) SSO, MFA, brokering out of the box; the API only validates JWTs.
- (−) +1 GB RAM (JVM). Dev mode with H2 — Keycloak data is ephemeral; lesson users are created by import.
