# 0005. Phone sign-in and registration in Keycloak

- **Status:** Accepted
- **Date:** 2026-11-15
- **Lesson / feature:** lesson-01

## Context
Users must be able to register with a phone number and sign in with a phone **or** a username (plus e-mail, Google, Microsoft). Local only; Keycloak does not support a phone number as a sign-in identifier out of the box.

## Decision
A custom Keycloak provider `deploy/keycloak/spi` (Java 21, Maven), built in a `docker build` stage and placed into the Keycloak image (`deploy/keycloak/Dockerfile`):
- **The phone is the profile attribute `phoneNumber`** (declarative user profile in the realm): required at registration/in the user profile, E.164 format (`^\+[1-9][0-9]{9,14}$`), e-mail is optional.
- **The `phone-unique` validator** — a number cannot belong to another user.
- **The `phone-username-password-form` authenticator** replaces the standard "username + password" form in the `browser` flow: an entered value that looks like a phone number (`+7 (999) 123-45-67`, `89991234567`, `79991234567`) is normalized and the user is looked up by `phoneNumber`; if exactly one is found, their username is substituted, followed by the standard password check, brute-force protection, and OTP. If there is no match, the input is unchanged and the response is the same as for a wrong username.
- **The `videoservice` login theme** (ru/en): labels "Phone, username or e-mail", error messages.
- The realm contains the **full set of standard authentication flows** (exported from Keycloak 26.8.2) — the only difference: the login form in `forms`.
- Tokens do not change: the API still sees `sub`, `preferred_username`, `aud`.

## Alternatives considered
| Option | Why rejected |
| --- | --- |
| Phone as the username | No separate "username"; the user cannot sign in with both |
| Third-party phone-provider extension | Tied to cloud SMS providers and Keycloak versions, not local |
| Our own sign-in service outside Keycloak | We lose SSO/brokering/PKCE/brute-force protection |
| SMS-OTP as a second factor now | Requires an SMS gateway; there is no local replacement without an external provider — deferred |

## Consequences
- (+) One token format for all sign-in methods; `make verify-auth` tests go through the whole form flow like a browser.
- (−) Java in the repository and +1 build stage (~1 min on first build, cached afterwards).
- (−) **The number is not confirmed by SMS** — someone else's number can be registered. Plan: an `ISmsSender` port + a local stub gateway (code in logs) and a "confirm phone" required action — a separate ADR.
- For Google/Microsoft sign-in, the profile also requires a phone (the first sign-in shows an "update profile" form).

## Next steps
- [x] security: threat model updated
- [x] qa: `scripts/verify-auth.py`, normalization unit tests (JUnit in the image build)
- [ ] phone confirmation by SMS (local gateway) — lesson 2
