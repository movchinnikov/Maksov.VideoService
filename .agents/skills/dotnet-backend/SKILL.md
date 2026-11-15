---
name: dotnet-backend
description: >-
  Implementation of .NET services (API, Feed) on the latest .NET from contracts in the Hexagonal +
  Vertical Slice style: minimal APIs, Npgsql/Dapper/EF Core, Keycloak (JWT, roles, external Google/Microsoft SSO),
  outbox, idempotency, MinIO presigned URLs, resource economy, async only for I/O,
  packaging shared libraries into NuGet. Use when writing server code.
---

# .NET Backend

## Input / output
- **Input:** `contracts/`, `docs/stories/<feature>.md`, QA's red tests.
- **Output:** code in `services/gateway`, `services/videos`, `services/users`, `services/analysis`, `services/comments`, `services/feed`, `libs/`, configs, `.env.example`.

## Structure (must read `.agents/rules/architecture.md`, ADR 0008)
- **Each service has its own solution** `services/<svc>/Maksov.VideoService.<Svc>.slnx` with its own `Directory.Build.props|targets`, `Directory.Packages.props`, `global.json`, `nuget.config`; shared MSBuild settings — `build/` (`Common.props|targets`, `Common.Packages.props`, templates). There is no single solution.
- **Libraries are the `libs/` solution** (`Primitives`, `BuildingBlocks`, `Web`, `TestSupport`) and **NuGet packages**: a service references them via `PackageReference` (version in `Common.Packages.props`, floating `0.1.0-local.*`); before building a service manually — `make pack` (→ `artifacts/nuget`). The Docker context is the repo root, `libs` are packed by the first Dockerfile stage.
- **The `Maksov.VideoService.` prefix** on every assembly and namespace; folder = assembly name.
- **Services:** `<Svc>.WebApi` (host/composition root) · `<Svc>.Bll` (ports) · `<Svc>.Dal` (data adapters, HTTP clients, migrations) · **`<Svc>.Features.<Area>`** (per-feature assemblies with slices). videos also has `Videos.Domain` (a project inside the service: statuses, `UploadPolicy`, `RenditionLadder`, `VideoEvents`), `Features.Feed`, the worker `Videos.MediaWorker`; analysis has `Analysis.Worker`. **The gateway** is `services/gateway` (YARP).
- **The shared web layer is the `Maksov.VideoService.Web` package**: Keycloak JWT (`KeycloakAuthExtensions`), `HttpExtensions` (errors → Problem Details, `UserId()`), `HealthProbe`, `InternalApiKey` (`RequireInternalKey()`). `Primitives` (Result, DomainError), `BuildingBlocks` (Postgres/SqlMigrator, S3/MinIO, OpenTelemetry, ProcessRunner, `MediaKeys`).
- Dependencies: `WebApi → Bll, Dal, Features, packages`; `Features → Bll, Domain, packages`; `Dal → Bll, BuildingBlocks`; **`Features` does not reference `Dal` and `WebApi`**; features do not reference each other; **services do not reference each other** (only HTTP/events).
- **DB-per-service:** its own DB and role per service (`deploy/postgres/init/00-databases.sh`; `videos`, `users`, `analysis`, `comments`), its own migrator (`--migrate`).
- **Internal endpoints** `/internal/v1/...` — `RequireInternalKey()`, not published through the gateway; a service behind the gateway validates the JWT itself. A synchronous call to another service (feed: videos → users `following-ids`) — a port in `Bll`, an HTTP adapter in `Dal`, a 20 s cache, degradation when unavailable.
- **Outbox — a row per (event, subscriber)** (`VideoEvents`: `video.processed.v1`, `video.deleted.v1` → `analysis`, `comments`); `OutboxDispatcher` (LISTEN/NOTIFY + a 30 s fallback poll, backoff 5 s … 5 min); receivers are idempotent; addresses `Outbox__Subscribers__<name>__BaseUrl`.
- **Visibility:** ready videos are visible to any authenticated user, the rest to the owner (otherwise 404); description — the owner sees any status, others only `done`. Video deletion — `deleted` + `video.deleted.v1` in one transaction, then MinIO cleanup (a cleanup failure does not cancel the deletion).
- **YARP:** the authorization policy is named `authenticated` (the name `default` is reserved); routes and clusters — `appsettings.json` (`ReverseProxy`), more specific routes — with a lower `Order`.

### How to add a feature (a new area) to a service
1. Create `services/<svc>/Maksov.VideoService.<Svc>.Features.<Area>/` (csproj modeled on `Videos.Features.Feed` / `Comments.Features.Comments`), add it to the service solution `Maksov.VideoService.<Svc>.slnx`.
2. Inside — a folder per use case: `<UseCase>/<UseCase>.cs` with `Handler` (depends only on ports), `Endpoint` (route), DTOs. Handlers are `internal`.
3. Public entry points of the assembly: `Add<Area>Features(services[, configuration])` and `Map<Area>Features(RouteGroupBuilder|IEndpointRouteBuilder)`.
4. New ports — in `<Svc>.Bll/Ports`, implementations — in `<Svc>.Dal` (registered in `DalServiceCollectionExtensions`), migrations — `Dal/Postgres/Migrations/NNNN_name.sql` + `.down.sql`.
5. In `<Svc>.WebApi/Program.cs` — two lines: `Add<Area>Features` and `Map<Area>Features`.
6. A public route — in the gateway's `ReverseProxy` and in `contracts/openapi/api.yaml`; an internal one — in `internal-<svc>.yaml`.
7. Tests: `<svc>/tests/Maksov.VideoService.<Svc>.Tests` (API with `TestSupport` and adapters on a real DB — both in the service's test project).

### How to add a new service
See "How to add a new service" in `.agents/rules/architecture.md` (solution and `Directory.*` from `build/`, assemblies, DB and role, migrator, gateway, event subscription, Dockerfile with a `libs` stage, compose with limits, tests, documentation). Reference — `services/comments`.

## Latest .NET and performance
- `net10.0`+ (the target TFM is currently set in `Directory.Build.props`), C# latest; `WebApplication.CreateSlimBuilder` where sufficient; `System.Text.Json` source generation (`JsonSerializerContext`) for all DTOs.
- Npgsql: a single `NpgsqlDataSource`, Dapper for reads/simple commands, EF Core — only if a complex change model is needed. Pagination — keyset (cursor), not `OFFSET`.
- Large data — by streaming (`Results.Stream`, `PipeReader`), without intermediate buffers.
- `async` — I/O only; CPU code is synchronous; bounded parallelism; `CancellationToken` everywhere.
- `HybridCache`/`IMemoryCache` with a size limit — only based on measurements.
- Logs — structured, with the `LoggerMessage` source generator on hot paths.

## Keycloak and SSO
- The API is a Resource Server: `AddAuthentication().AddJwtBearer()` with Keycloak's `Authority`/`MetadataAddress`, validating `iss`, `aud` (audience mapper `videoservice-api`), `exp`, algorithm RS256.
- In Docker: `MetadataAddress` is the internal Keycloak address (`http://keycloak:8080/...`), `ValidIssuer` is the public one (`http://localhost:8080/realms/<realm>`); Keycloak with `KC_HOSTNAME` + `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`.
- Roles: `realm_access.roles` → `ClaimsPrincipal` via `IClaimsTransformation`; policies `AddAuthorizationBuilder().AddPolicy(...)`. The resource owner is `sub` from the token, never from the request body.
- Phone login: a custom provider `deploy/keycloak/spi` (authenticator `phone-username-password-form` + validator `phone-unique`); the application does not see it — the token is the same. The phone is the `phoneNumber` attribute in E.164 format.
- External SSO: Google and Microsoft (Entra ID) are connected in Keycloak as Identity Providers (Identity Brokering), the application does not know about them. IdP secrets — only via env/the setup script (`deploy/keycloak/configure-idps.sh`), first-broker-login flow linking by e-mail only for verified e-mail.
- Web is a public client with PKCE (S256), no client secret; service-to-service — a confidential client + client credentials.
- Tests: generating test JWTs with a local key (`TestAuthHandler` or replacing `TokenValidationParameters`), for integration — Keycloak in Testcontainers.

## Key patterns
- **Outbox** (lesson 4+): business data + `outbox` in one transaction; a publisher with `enable.idempotence=true`, `acks=all`.
- **Task queue on Postgres** (lessons 1–3): `jobs` + `FOR UPDATE SKIP LOCKED`, lease (`locked_until`), attempts, backoff; enqueueing a task — in the same transaction as the status change.
- **Inbox / idempotency:** deduplication by `event_id`/`job_id`; status transitions only forward.
- **Idempotency-Key** for POST APIs.
- **MinIO:** presigned PUT (with a short TTL, `Content-Type` restriction), object verification (`HEAD`: size, type) before enqueueing a task. Presign — with the client using the public endpoint, operations — with the client using the internal one.
- **Health:** `/health/live`, `/health/ready`.

## NuGet
Code shared between services (hosting defaults, Postgres/migrations, storage, `MediaKeys`, the web layer, test support) lives in `libs/*` (`Maksov.VideoService.*`) with `IsPackable=true`; version `0.1.0` (`VersionPrefix` in `build/Common.props`) + suffix `local.<hash of sources>` (`make pack`; `VERSION_SUFFIX` in CI). `make pack` → `artifacts/nuget`; services take packages from this feed (`nuget.config`), not `ProjectReference`. The library's public API is stable (`PublicApiAnalyzers` as it matures).

## Steps
1. Read the contract and ACs; make sure red tests exist.
2. Create a slice, DTOs strictly per the contract.
3. Implement minimally → tests green → refactor.
4. Migrations — per the schema from `dba`.
5. New variables — in `.env.example`; the service — in compose (per the `devops` template).
6. `make lint`, `make test`, `make up && make demo`.

## Done when
- [ ] The build has no warnings, `make lint` is green.
- [ ] All tests are green, ACs are covered.
- [ ] Handlers are idempotent (redelivery test).
- [ ] Endpoints require authorization, the owner is checked by `sub`.
- [ ] No buffering of large data, `async` only for I/O.
- [ ] The feature comes up locally and is visible in `make demo`; README / `.env.example` are updated.
