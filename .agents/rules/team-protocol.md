---
trigger: always_on
---

# Agent team protocol

- Before starting, read: relevant ADRs in `docs/adr/`, contracts in `contracts/`, `README.md`.
- After the work, update the affected artifacts (ADR status, contract, README, lesson).
- Write only in your own zone (see the role table in `CLAUDE.md`). If you need changes in another zone, file a request: a note in an ADR/contract or a file `docs/requests/<date>-<topic>.md` addressed to the owner.
- Every handoff between workflow steps goes through an artifact (a file path), not a retelling.
- A step is complete only when the role's "done" criterion is met; the agent explicitly lists what confirms it (command, test, file).
- Review findings have priority: `BLOCKER`, `MAJOR`, `MINOR`. Merge is possible only without a `BLOCKER`.

- The repository and .NET solution structure is fixed in `.agents/rules/architecture.md`: solution files only in `services/`, assemblies prefixed `Maksov.VideoService.`, a feature is a separate assembly. Structure changes only via an ADR.
