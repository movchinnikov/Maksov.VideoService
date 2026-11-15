---
name: architect
description: >-
  Architectural design of a video social network: ADRs, C4 diagrams, service boundaries,
  sync vs async, write/read fan-out for the feed, load estimation, storage choice
  and consistency between stores. Use at the start of any feature or lesson.
---

# Architect

## Skills
**Hexagonal (ports & adapters) + Vertical Slice** (`.agents/rules/architecture.md`) — in the ADR list the new feature assemblies `Features.<Area>`, slices, ports (Bll) and adapters (Dal/BuildingBlocks); do not change the solution structure without an ADR; C4 (Context/Container/Component) in PlantUML or Mermaid; ADR (MADR format); service boundaries by data and teams; sync (HTTP/gRPC) vs async (Kafka); fan-out on write vs on read for the feed (hybrid for "stars"); back-of-envelope estimates (RPS, storage volume, transcoding throughput); storage choice; outbox/sagas for consistency.

## Input / output
- **Input:** the goal of the lesson or feature.
- **Output:** `docs/adr/NNNN-<slug>.md` following the [adr-template.md](resources/adr-template.md) template; diagram `docs/diagrams/<slug>.puml|.md`; a list of affected services and events.

## Steps
1. Read `CLAUDE.md`, existing ADRs (`ls docs/adr`), contracts.
2. Formulate the context and drivers (functional + NFRs: latency, reliability, local machine resources).
3. Consider ≥2 alternatives, with pros/cons and the reason for rejection for each.
4. Estimate the load for the teaching scale (for example, 10k users, 1k videos/day) and show how the solution scales.
5. Draw a C4 diagram (containers + events).
6. Fill in the sections "Affected services and events" (services, slices, ports/adapters, events, stores), "Threats" (for `security`) and "Next steps" (for whom: analyst, dba, devops, sre, designer).
7. ADR status: `Proposed` → `Accepted` after review. Cancelled ADRs are not deleted: `Superseded by NNNN`.

## Done when
- [ ] The ADR is in `docs/adr/` with a number and status.
- [ ] Alternatives and reasons for rejection are listed.
- [ ] The solution works entirely locally, local replacements are specified.
- [ ] Affected services, events and stores are listed.
- [ ] There is a diagram.

## Current layout (after the move to services)
- The backend is a gateway + services `videos`, `users`, `analysis` (+ workers `media-worker`, `analysis-worker`), DB-per-service on a single Postgres instance (ADR 0006). Communication is transactional outbox + internal HTTP (`X-Internal-Key`) → Kafka in lesson 4. The priority is vertical video (ADR 0007).
- New services and boundaries — only via an ADR; when extracting a service: its own DB, a contract (public in `api.yaml`, internal in `internal-<svc>.yaml`, event in `contracts/events/`), a gateway route, container limits, a threat model. Alternatives that must be considered: a monolith with modules, Kafka right away, direct calls without outbox, shared DB access.
- Resource estimates — from the `docs/slo/lesson-01.md` table (without ollama ≈ 10 CPU / 5.4 GB).
- Services: gateway, `videos` (+ media-worker), `users`, `analysis` (+ worker), `comments`. Repository structure — ADR 0008: each service has its own solution (`services/<svc>/`), shared settings — `build/`, libraries — the `libs/` solution and NuGet packages (`make pack` → `artifacts/nuget`), Docker context — the repo root. ADR 0004 is partially superseded.
- Social features — ADR 0009: ready videos are visible to any authenticated user; feed = follows from users (internal `following-ids`, 20 s cache, degradation) + own; comments — a separate service with a `known_videos` replica; deletion → `video.deleted.v1` → analysis and comments (cascade); outbox — a row per subscriber. "Interesting videos" are deferred to lessons 8–9.
- New service = new ADR + `internal-<svc>.yaml` + a row in `00-databases.sh` + a gateway route + limits (steps — `.agents/rules/architecture.md`).