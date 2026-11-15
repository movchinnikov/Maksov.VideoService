---
name: frontend-react
description: >-
  Web client on React + TypeScript + Vite: OIDC (Keycloak, PKCE), video upload via presigned URL
  with progress and cancellation, polling of processing status, Shaka player (DASH/HLS, ABR, quality selection),
  list of own videos, design-system implementation. Use for any work in clients/web.
---

# Frontend React

## Input / output
- **Input:** `contracts/openapi/`, ACs, `docs/design/`.
- **Output:** `clients/web/`.

## Structure
```
clients/web/src/
  app/            # router, providers (auth, query)
  features/<area>/<use-case>/   # vertical UI slices: component + hook + api call
  shared/{api,ui,lib}/          # typed client, UI primitives, utilities
  styles/         # tokens and base styles (owner — designer)
```

## Key mechanics
- **Auth:** `oidc-client-ts` (`UserManager`, code+PKCE), `InMemoryWebStorage` for the user store, silent renew via refresh token; protected routes; `Authorization: Bearer` in the fetch wrapper.
- **Upload:** `POST /api/videos` → presigned URL → `XMLHttpRequest.upload.onprogress` (fetch does not report upload progress) → `POST /api/videos/{id}/complete`. Cancellation via `xhr.abort()`. Client-side size/type checks are UX only, not protection.
- **Status:** polling `GET /api/videos/{id}` with backoff (1→2→5 s) until `Ready`/`Failed`; stop when leaving the page.
- **Player:** Shaka Player loaded lazily (`import()`), `registerRequestFilter` adds the token, DASH/HLS protocol selection, manual quality selection + Auto (ABR), cleanup with `player.destroy()` on unmount.
- **Errors:** Problem Details → human-readable messages.

## Quality
ESLint + `tsc --noEmit` + Vitest/Testing Library; bundle budget; axe in tests of key screens.

## Done when
- [ ] lint/typecheck/test/build are green.
- [ ] AC scenarios pass on the local stack.
- [ ] All screen states from `docs/design/` are implemented.
