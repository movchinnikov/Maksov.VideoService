---
name: code-review
description: >-
  Review of a diff against the project checklist: security, idempotency, races, resource leaks,
  unsafe migrations, contract violations, container limits, tests, documentation.
  Produces findings with BLOCKER/MAJOR/MINOR priority.
---

# Code Review

## Input / output
- **Input:** `git diff <base>...HEAD`, related ADRs and contracts.
- **Output:** `docs/reviews/<branch>.md`: verdict (`APPROVE` / `CHANGES_REQUESTED`) + a findings table `| Priority | File:line | Problem | Suggestion | Assignee |`.

## Automated checks (run first)
`make lint`, `make test`, `dotnet build -warnaserror`, contract lint, schema compatibility check.

## Checklist
**Contracts:** the code matches OpenAPI/schemas; schema changes are backward compatible; the contract is updated before the code.
**Idempotency:** the consumer deduplicates by `event_id`; the offset is committed after processing; side effects (MinIO, Qdrant, external calls) are repeatable; there is a redelivery test.
**Consistency:** the DB write and the event go through the outbox; no "dual write".
**Concurrency:** no races on counters (atomic operations), optimistic locks where needed, no `async void`, no `.Result/.Wait()`.
**Resources:** `using`/`await using`, cancellation via `CancellationToken`, timeouts on ffmpeg/HTTP, temporary files are deleted, container limits are set.
**Migrations:** reversible, without long locks (`CONCURRENTLY`, expand/contract), no data loss.
**Security:** no secrets, input validation, upload size limits, presigned URLs with a short TTL, authorization on every endpoint, SQL only parameterized.
**Structure:** solution files only in `services/`; the `Maksov.VideoService.` prefix; assembly dependencies follow the graph from `.agents/rules/architecture.md` (`Features` do not reference `Dal`/`WebApi`/each other); a new area is a separate `Features.<Area>`.
**Architecture:** a slice is self-contained; the domain does not depend on infrastructure; adapters are behind ports.
**Performance:** latest .NET; no buffering of large data; `async` only for I/O, no `Task.Run` in ASP.NET; bounded parallelism; no polling without backoff; JSON source generation.
**Auth:** the endpoint is under a policy; the owner comes from `sub`; IDOR → 404.
**Observability:** traces/metrics/logs, `trace_id` in events, no PII logging.
**Tests and documentation:** ACs are covered; README/lesson/`.env.example` are updated.

## Priorities
- `BLOCKER` — data loss/corruption, contract violation, non-idempotent handler, secret in code, failing tests.
- `MAJOR` — resource leak, missing tests for ACs, missing limits, bad migration.
- `MINOR` — style, readability, suggestions.

## Done when
- [ ] No `BLOCKER`; the verdict is recorded.
- [ ] If the problem can be caught automatically — a proposal to add a check to the linters/checklist.
