---
name: devops-k8s
description: >-
  Local infrastructure: docker-compose, Makefile (up/down/test/lint/demo/pack), Keycloak
  (realm import, IdP), image builds, packaging shared .NET libraries into NuGet (local feed,
  versions, CPM), Helm, kind/k3d, Strimzi, KEDA, CI via GitHub Actions/act.
---

# DevOps / K8s

## Input / output
- **Input:** the list of services and resources (ADR, PR).
- **Output:** `deploy/compose/*.yml`, `deploy/keycloak/`, `deploy/helm/<chart>`, `Makefile`, `.github/workflows/`, NuGet packages.

## Compose (lessons 1–10)
- `deploy/compose/docker-compose.yml` + profiles for the heavy parts (`--profile data`, `--profile ml`, `--profile observability`).
- Every service has: a pinned image version, `healthcheck`, `deploy.resources.limits` (cpus, memory), `depends_on: condition: service_healthy|service_completed_successfully`.
- One-shot containers: `minio-init` (buckets, CORS), `migrator` (SQL migrations), later `kafka-init`.
- Keycloak: a custom image from `deploy/keycloak/Dockerfile` (the maven stage builds the SPI `deploy/keycloak/spi` — phone login/registration and the theme), `start-dev --import-realm`, the realm in `deploy/keycloak/realm-*.json` (no secrets), external IdPs — the `configure-idps.sh` script via `kcadm.sh` driven by env. The realm is imported only into an empty Keycloak DB: after realm edits — `make down && make up`.
- The client's redirect URIs must list **both `localhost` and `127.0.0.1`** (otherwise "Invalid parameter: redirect_uri" when opened via a different host).
- `.env.example` in the root; `make up` copies it to `.env` if missing.

## .NET images
- The build context of .NET images is the **repository root**; the Dockerfile lives in the service folder (`services/<svc>/Dockerfile`; videos and analysis also have `Dockerfile.worker`). The first `libs` stage packs `libs/` into `/feed` (version `local.<hash>`), the second copies `build/`, the feed and the service folder and runs `dotnet publish`.
- Multi-stage: `sdk` → `aspnet`/`runtime` (`-noble-chiseled` where no shell/apt is needed), non-root user `app`, `DOTNET_gcServer=0` for small services, `InvariantGlobalization` where possible.
- Restore cache: BuildKit cache `/root/.nuget/packages`; the hash of the libs sources in the package version prevents the cache from serving a stale package.
- Media worker: runtime + ffmpeg from distribution packages (the version is pinned by the image tag).

## NuGet
- `libs/*` — `IsPackable=true`; services — `IsPackable=false`. Shared MSBuild settings — `build/` (`Common.props|targets`, `Common.Packages.props`, `global.json`/`nuget.config` templates); `Directory.*`, `global.json`, `nuget.config` live in each service folder and in `libs/` (ADR 0008).
- Version: `VersionPrefix` in `build/Common.props`, suffix `local.<hash of libs and build sources>` (`make pack`), in CI `VERSION_SUFFIX=ci.<run>`; releases — by git tag.
- `make pack` → `dotnet pack` in the SDK container, the result is `artifacts/nuget` (not in git; the services' `nuget.config` connects `../../artifacts/nuget`); an external consumer adds this directory as a package source.
- Central Package Management (each solution's `Directory.Packages.props` → `build/Common.Packages.props`), `packages.lock.json` + `--locked-mode` in CI.
- SourceLink and deterministic build (`ContinuousIntegrationBuild=true` in CI).

## Makefile (target contract)
| Target | What it does |
| --- | --- |
| `up` | brings the stack up from scratch, waits for healthy |
| `down` | stops and removes the project's containers/volumes |
| `env-sync` | appends new variables from `.env.example` to `.env`; `up` depends on it |
| `test` | `test-dotnet` (packs libs, temporary Postgres, tests of every solution: `libs` and services) + `test-web` |
| `lint` | `-warnaserror` build and `dotnet format --verify-no-changes` for every solution, eslint/tsc, contract linters |
| `demo` | the end-to-end scenario of the current lesson with checks |
| `pack` | NuGet packages into `artifacts/nuget` |

## K8s (lesson 11+)
kind/k3d, a Helm chart per service + umbrella, Strimzi, KEDA `ScaledObject` by lag/queue depth, requests/limits are mandatory.

## Done when
- [ ] `make up` from a clean machine brings the stack up; `make down` cleans everything up.
- [ ] Total limits fit into ~16 GB RAM / 8 CPU (table in the README).
- [ ] `make pack` builds packages with the correct version.

## Current layout (after the move to services and social features)
- **Compose services:** `postgres`, `minio`, `keycloak`, `videos` (+ `videos-migrator`), `media-worker`, `users` (+ `users-migrator`), `analysis` (+ `analysis-migrator`), `analysis-worker`, `comments` (+ `comments-migrator`), `gateway` (host port 8081), `web` (3000); `ollama` — the `ml` profile (`make up-ml`, port 11434 on localhost only). `videos`, `users`, `analysis`, `comments` **do not publish** ports to the host.
- **Postgres:** one instance, a DB and role per service — `deploy/postgres/init/00-databases.sh` (passwords `VIDEOS_DB_PASSWORD`, `USERS_DB_PASSWORD`, `ANALYSIS_DB_PASSWORD`, `COMMENTS_DB_PASSWORD`); the migrator is a one-shot container with `--migrate`, the service depends on `service_completed_successfully`.
- **Variables (`.env.example`):** `INTERNAL_API_KEY` (inter-service key), `*_DB_PASSWORD`, `ANALYSIS_CAPTIONER` (`none`/`ollama`), `OLLAMA_MODEL`. In compose, media-worker gets the outbox subscribers `Outbox__Subscribers__analysis__BaseUrl` and `…__comments__BaseUrl` (the dispatcher does not start without addresses), videos gets `Users__BaseUrl`, `Users__InternalApiKey` (feed). Add a new variable to `.env.example`; `make env-sync` appends it to an existing `.env`.
- **Images:** `videoservice/{videos,users,analysis,analysis-worker,comments,gateway,media-worker,web}:local`; workers are built from `Dockerfile.worker` (with ffmpeg). The build context is the repository root.
- **Limits:** postgres 1.0/512M, minio 1.0/512M, keycloak 1.5/1G, videos 1.0/256M, media-worker 2.0/1.5G, users 0.5/192M, analysis 0.5/256M, analysis-worker 1.0/768M, comments 0.5/192M (+ comments-migrator 0.5/192M), gateway 0.75/192M, web 0.25/64M, ollama 4.0/4G. Total without ollama ≈ 10 CPU / 5.4 GB.
- **Make:** `up` (depends on `env-sync`), `up-ml`, `down` (with the `ml` profile), `env-sync`, `pack` (`Primitives`, `BuildingBlocks`, `Web`, `TestSupport`), `test` / `test-dotnet` (per solution, temporary Postgres) / `test-web`, `demo` (second user `demo-friend`: follow, feed, comments, deletion), `verify-auth`, `lint`, `logs s=<service>`. The solution list is `libs` + `services/*/Maksov.VideoService.*.slnx` (picked up automatically).
- For k8s (lesson 11): service = Deployment + Service, migrator = Job/initContainer, `/internal` without Ingress, NetworkPolicy "gateway → services only".
