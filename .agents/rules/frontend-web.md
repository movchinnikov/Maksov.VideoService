---
trigger: glob
globs: "clients/web/**"
---

# Web client

- React 19 + TypeScript strict + Vite. No out-of-the-box UI kits (MUI/Chakra/shadcn defaults) — the design system is custom, per `docs/design/`.
- Server state — TanStack Query (or a simple fetch hook at the start); a global store only when really needed.
- Authorization — OIDC Authorization Code + PKCE against Keycloak (`oidc-client-ts`); tokens in memory, not in `localStorage`.
- Player — Shaka Player (DASH and HLS from the same CMAF segments); the token is added via a request filter.
- The API client is typed from `contracts/openapi/` (`openapi-typescript` generation).
- Accessibility: semantic markup, focus, AA contrast, keyboard support.
- Budget: initial JS ≤ 200 KB gzip (the player is lazy-loaded).
