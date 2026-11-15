---
name: wf-feature
description: >-
  The main "feature or lesson" cycle: architect → analyst + designer → security (threat model) →
  QA (red tests) → DBA → developers (backend, video, frontend, ML, data, Android) → reviewer +
  security → DevOps + SRE → QA (final run) → lesson writer. Use when the user
  asks to implement a feature or a new lesson of the series (/wf-feature <goal>).
---

# Workflow: feature or lesson

The orchestrator (the main agent) launches subagents from `.agents/agents/` step by step, passes **artifact paths**, and checks the "done" criterion before moving on. Input: the goal (`$GOAL`), the lesson number `NN` (if a lesson).

| # | Agent | Input | Output (artifact) | Transition gate |
| --- | --- | --- | --- | --- |
| 1 | `architect` | `$GOAL` | `docs/adr/NNNN-*.md`, `docs/diagrams/*` | ADR `Proposed`: alternatives, slices/ports/adapters, threats |
| 1a | user | ADR | `Accepted` | **user confirmation** |
| 2 | `analyst` ∥ `designer` | ADR | `contracts/**`, `docs/stories/*` ∥ `docs/design/*` | contract lint/compatibility, AC with IDs; anti-AI design checklist |
| 3 | `security` | ADR + contracts | `docs/security/threat-model-*.md` | threats and mitigations carried over into AC |
| 4 | `qa` (mode A) | AC + contracts | red tests | fail for the right reason |
| 5 | `dba` *(if storage changes)* | ADR + contracts | migrations, `docs/db/*` | apply/roll back |
| 6 | developers *(in parallel by zone)*: `dotnet-backend`, `video-engineer`, `frontend-react`, `ml-engineer`, `data-engineer`, `android-dev` | contracts, AC, tests, design | code | lint/test green per zone |
| 7 | `reviewer` ∥ `security` | diff + contracts | `docs/reviews/*`, `docs/security/pentest-*` | no `BLOCKER` and no High/Critical; otherwise → step 6 (max 3 rounds) |
| 8 | `devops` ∥ `sre` | list of services | `deploy/**`, `Makefile`, NuGet ∥ SLO, dashboards, alerts, runbooks | `make down && make up && make demo` from scratch |
| 9 | `qa` (mode B) | everything | AC → test matrix, reports | all AC covered and green |
| 10 | `lesson-writer` *(for a lesson)* | ADR, diff, demo | `docs/lessons/NN-*.md`, README, tag | reproduction from a clean clone |

## Orchestration rules
- Give every subagent: the role, input artifacts, expected output, the "done" criterion, and a reminder to read `CLAUDE.md` and its own skill.
- `∥` — in parallel. Step 6 is parallel only for independent zones; contracts are frozen by this point.
- Requests into another zone go through the orchestrator to the owner.
- Run log — `docs/runs/<date>-<feature>.md` (not in git).
- Before committing — check `.agents/rules/privacy-ai.md`.
- Final: a brief report to the user with links to the ADR, contracts, review, lesson.

## Relevant to the repository structure
- Developers write into their service's folder (`services/<svc>/`, which has its own solution); shared code — `libs/` (packed by `make pack`, services consume packages). A new service — step by step in `.agents/rules/architecture.md` (including event subscription and a gateway route).
- Final QA run: `make test` (per solution), `make lint`, `make demo` (including social features: a second user, follow, feed, comments, deletion). Resources without ollama ≈ 10 CPU / 5.4 GB.
- Artifacts: ADR 0006–0009, contracts `contracts/openapi/{api,internal-analysis,internal-comments,internal-users}.yaml` and `contracts/events/{video-processed,video-deleted}.v1.json`.
