---
name: wf-review
description: >-
  Change review workflow: collecting the diff and related contracts, automated checks,
  checklist review by the reviewer subagent, returning findings to owners, re-review.
  Use before merging any branch (/wf-review [base]).
---

# Workflow: review

1. The **orchestrator** collects the input: `git diff ${BASE:-main}...HEAD --stat` and the full diff; from the changed paths it determines the related contracts (`contracts/**`) and ADRs.
2. The **orchestrator** runs `make lint` and `make test`; passes the results to the reviewer.
3. The **`reviewer`** (skill `code-review`) checks: security, idempotency, migrations, event schemas, resources, tests, documentation. Writes `docs/reviews/<branch>.md`.
4. The **orchestrator** groups findings by addressee (zone by file path, see the table in `CLAUDE.md`) and passes them to the corresponding subagents.
5. Owners fix; the **`reviewer`** rechecks only what changed and closes the findings.
6. Output: verdict `APPROVE` when there is no `BLOCKER`. After 3 failed rounds — escalate to the user.
7. If a found problem could have been caught automatically — a task to add a rule to the linter / the `code-review` checklist.
