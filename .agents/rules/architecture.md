---
trigger: glob
globs: "services/**,libs/**,build/**,clients/web/**"
---

# Architecture: Hexagonal + Vertical Slice, .NET solution structure

## Root rule (ADR 0008)
- **Each service has its own solution** in its own folder: `services/<svc>/Maksov.VideoService.<Svc>.slnx` (gateway, videos, users, analysis, comments) with its own `Directory.Build.props|targets`, `Directory.Packages.props`, `global.json`, `nuget.config`. There is no single solution in `services/`.
- **Shared MSBuild settings live in `build/`:** `Common.props` (TFM, analyzers, `VersionPrefix`), `Common.targets`, `Common.Packages.props` (third-party package versions and shared library versions `$(VideoServiceLibsVersion)` = `0.1.0-local.*`), templates `global.json.template`, `nuget.config.template`. A solution's `Directory.*` files only import these. The root `.editorconfig` and `.dockerignore` are shared.
- **Shared libraries are the `libs/` solution** (`Maksov.VideoService.Libs.slnx`) and **NuGet packages**: services consume them via `PackageReference`, not `ProjectReference`. `make pack` → `artifacts/nuget` (not in git), version `0.1.0-local.<hash of libs and build sources>`; the service's `nuget.config` adds the feed `../../artifacts/nuget` and maps `Maksov.VideoService.*` to it only.
- **The Docker build context is the repository root:** the first Dockerfile stage packs `libs/` into `/feed` (version by hash), the second copies `build/`, the feed and one service folder, then runs `dotnet publish`. A service cannot see its neighbors' sources.
- The repository root holds only cross-service items: `Makefile`, `README.md`, `docs/`, `contracts/`, `deploy/`, `clients/`, `scripts/`, `build/`, `libs/`, `services/`, `.env.example`, `.gitignore`. A new .NET project creates no solution files or shared MSBuild settings outside its service folder / `build/`.

## Naming
The prefix of all assemblies and namespaces is **`Maksov.VideoService.`** (project folder = assembly name = root namespace).
Directories: `Maksov.VideoService.<Layer or Feature>[.<Subfeature>]`. Docker images are `videoservice/<svc>:local`; configuration keys use .NET style (`Outbox__Subscribers__<name>__BaseUrl`).

## Layout
```
build/                                    Common.props|targets, Common.Packages.props, global.json / nuget.config templates
libs/                                     libraries solution → NuGet (make pack)
  Maksov.VideoService.Primitives          Result, DomainError
  Maksov.VideoService.BuildingBlocks      Postgres/SqlMigrator, S3/MinIO, OpenTelemetry, ProcessRunner, MediaKeys
  Maksov.VideoService.Web                 Keycloak JWT, HttpExtensions (errors → Problem Details), HealthProbe, InternalApiKey
  Maksov.VideoService.TestSupport         test authentication, temporary DBs (for test projects)
  tests/Maksov.VideoService.BuildingBlocks.Tests
services/
  gateway/        Maksov.VideoService.Gateway (YARP, :8081; routes in appsettings.json), tests/
  videos/         Videos.{WebApi,Bll,Dal,Domain,Features.Videos,Features.Feed,MediaWorker}, tests/{Videos.Tests,Videos.MediaWorker.Tests}, Dockerfile, Dockerfile.worker
  users/          Users.{WebApi,Bll,Dal,Features.Profiles}, tests/
  analysis/       Analysis.{WebApi,Bll,Dal,Features.Descriptions,Worker}, tests/, Dockerfile, Dockerfile.worker
  comments/       Comments.{WebApi,Bll,Dal,Features.Comments}, tests/, Dockerfile
  (in each service folder: <Svc>.slnx, Directory.*, global.json, nuget.config)
```
Layer roles inside service `<Svc>`:
- `<Svc>.WebApi` — host and composition root: `Program.cs`, authentication, health, the `--migrate` command.
- `<Svc>.Bll` — the service's ports (interfaces) and business logic.
- `<Svc>.Dal` — port adapters (Postgres/Dapper, HTTP clients of other services) and SQL migrations.
- `<Svc>.Features.<Area>` — vertical slices of an area (endpoint + handler + DTO + validation).
- `Videos.Domain` — the video service domain (statuses, `UploadPolicy`, `RenditionLadder`, `VideoEvents`); a regular project inside the service, not a library.
- Workers (`Videos.MediaWorker`, `Analysis.Worker`) — the service's background-job hosts with their own Dockerfile; slices in `Features/`, ports in `Ports/`, adapters in `Adapters/`.

## Dependency graph (arrow = "references")
```
<Svc>.WebApi ──► <Svc>.Bll, <Svc>.Dal, <Svc>.Features.*, packages Web, BuildingBlocks
<Svc>.Features.* ─► <Svc>.Bll, <Svc>.Domain (if present), packages Web, Primitives, BuildingBlocks
<Svc>.Dal ───► <Svc>.Bll, package BuildingBlocks
<Svc>.Bll ───► <Svc>.Domain (if present), package Primitives
Workers ─────► Bll/Dal/Domain of their own service, package BuildingBlocks
libs/Web ────► libs/Primitives (ProjectReference inside libs/)
Gateway ─────► packages Web, BuildingBlocks
Test projects ─► service projects, package TestSupport
```
Forbidden: `Features.* → Dal`, `Features.* → WebApi`, `Bll → Dal/Web`, cycles, one feature referencing another, **references between services** (including a `ProjectReference` into a neighbor's folder) and project references to `libs/` (packages only). Services communicate via HTTP/events, not through assemblies or another service's DB.
Enforced by the build (separate solutions), an architecture test (NetArchTest, planned) and review.

## Rules
- **A new area = a new assembly `Maksov.VideoService.<Svc>.Features.<Area>`**; inside it, folders per use case `<UseCase>/` with a slice. The assembly exposes exactly two public methods to the host: `Add<Area>Features(IServiceCollection[, IConfiguration])` and `Map<Area>Features(RouteGroupBuilder|IEndpointRouteBuilder)`. Handlers are `internal`.
- **Ports** (`IVideoRepository`, `IFollowingProvider` …) are declared in the service's `Bll`, **implementations** in its `Dal` (or in `BuildingBlocks` if both workers and other services need them, like `IObjectStorage`, `MediaKeys`). The domain and handlers know nothing about ASP.NET, Npgsql, the AWS SDK.
- Each assembly registers its dependencies with an extension method; `Program.cs` only calls them and contains no business logic.
- The JSON context (source generation) lives **in the feature assembly** and is registered by its `Add…Features`.
- Schema migrations are embedded resources of the service's `Dal` (`Postgres/Migrations/NNNN_name.sql` + `.down.sql`); each service has its own migrator (`--migrate`) and its own DB — **one migrations table per DB, no shared DBs between services**.
- **DB-per-service:** a service reads and writes only its own DB; other services' data is reached via their API (internal, with `X-Internal-Key`) or events (outbox). A replica of foreign data is created from an event (example: `known_videos` in comments).
- A slice owns its request/response/SQL. Shared code is extracted only after a second real use; code shared between services goes into `libs/`.
- An endpoint is thin: parse → call the handler → map the result. Business-logic errors go through `Result<T>`/`DomainError`.
- No "MediatR for the sake of MediatR": calling the handler directly via DI is enough.
- Adapters are replaceable: the Postgres+HTTP queue/outbox (lesson 1) is replaced by Kafka (lesson 4) without changing the slice.
- **The outbox holds one row per (event, subscriber)**; subscribers and paths are in `VideoEvents` (videos Domain); addresses are `Outbox__Subscribers__<name>__BaseUrl`. Receivers are idempotent.
- **A synchronous inter-service call** (example: feed, videos → users `following-ids`) goes only through a port in `Bll` and an HTTP adapter in `Dal`, with a short cache, degradation when the peer is unavailable, and the `X-Internal-Key` key.
- Internal endpoints live under `/internal/v1/...`, are guarded by `RequireInternalKey()` (`X-Internal-Key`) and are not published through the gateway. Public ones go only through the gateway; a service behind the gateway validates the JWT itself (zero trust).
- Tests: `<service>/tests/Maksov.VideoService.<Svc>.Tests` (next to the service, in its solution; `libs/tests` is for libraries). `InternalsVisibleTo` is set via `<InternalsVisibleTo Include=… />` in the csproj. API tests use `TestSupport` (test authentication, temporary DBs).
- Docker: the build context is the repository root; the service's Dockerfile is in its folder (videos and analysis also have `Dockerfile.worker`); the first stage packs `libs/`; restore is cached.

## How to add a new service
1. **Contract and ADR:** public routes go in `contracts/openapi/api.yaml`, internal ones in `contracts/openapi/internal-<svc>.yaml`, events in `contracts/events/<name>.v1.json`. Record the decision in an ADR.
2. **Folder and solution:** `services/<svc>/` with `Maksov.VideoService.<Svc>.slnx`, `Directory.Build.props|targets` (importing `build/Common.props`), `Directory.Packages.props` (importing `build/Common.Packages.props`), `global.json` and `nuget.config` (copies of the templates from `build/`, feed `../../artifacts/nuget`) — modeled on `services/comments`.
3. **Assemblies:** `Maksov.VideoService.<Svc>.{WebApi,Bll,Dal,Features.<Area>}` (graph above), shared libraries via `PackageReference` on `Maksov.VideoService.{Primitives,BuildingBlocks,Web}`; tests in `tests/Maksov.VideoService.<Svc>.Tests` (package `TestSupport`).
4. **Data:** its own DB and role — a line in `deploy/postgres/init/00-databases.sh`, password `<SVC>_DB_PASSWORD` in `.env.example` (`make env-sync` appends it to an existing `.env`); migrations in `Dal`, the `--migrate` command, a migrator container in compose.
5. **Host:** `Program.cs` — JWT (package `Web`), health, `Add/Map…Features`; internal routes use `RequireInternalKey()`.
6. **Gateway:** a route and cluster in `services/gateway/.../appsettings.json` (`AuthorizationPolicy: authenticated`; more specific routes get a lower `Order`); a `GatewayTests` test. The service publishes no ports externally.
7. **Events:** if the service is a subscriber — an internal `POST /internal/v1/events/...`, add it to `VideoEvents.SubscribersOf` and `Outbox__Subscribers__<name>__BaseUrl` of media-worker; if it is a source — a schema in `contracts/events/` and the outbox.
8. **Docker/compose:** a `Dockerfile` in the service folder (modeled on `services/videos/Dockerfile`: stages `libs` → `build` → `runtime`, context is the repo root), the service and migrator in `deploy/compose/docker-compose.yml` with `cpus`/`mem_limit`, healthcheck, pinned images.
9. **Make:** `SOLUTIONS` in the `Makefile` picks up `services/*/Maksov.VideoService.*.slnx` automatically (`make test-dotnet`, `make lint`); `make pack` does not change.
10. **Documentation:** README (service table, ports, limits, troubleshooting), `docs/slo/` (resources, runbook), threat model, acceptance criteria in `docs/stories/`, a step in `scripts/demo.sh`.
