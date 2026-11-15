---
name: wf-incident
description: >-
  Incident and post-incident workflow: reproduction, a bug test, the fix, review, a postmortem
  in docs/postmortems/, updating checklists. Use for a bug, a service crash,
  data loss/duplication (/wf-incident <description>).
---

# Workflow: incident and postmortem

| # | Agent | Action | Artifact / gate |
| --- | --- | --- | --- |
| 1 | orchestrator | Collect symptoms: logs (Loki), traces, metrics, steps | postmortem draft |
| 2 | zone owner (by service) + `devops` | Reproduce locally (`make up`, scenario) | reproduction steps |
| 3 | `qa` | A test that reproduces the bug | test is red |
| 4 | zone owner | Fix the root cause (not the symptom) | test is green, `make test` |
| 5 | `reviewer` | Review of the fix (`wf-review`) | no `BLOCKER` |
| 6 | `lesson-writer` (or orchestrator) | Postmortem following [postmortem-template.md](resources/postmortem-template.md) | `docs/postmortems/YYYY-MM-DD-<slug>.md` |
| 7 | `reviewer` / process owner | If it could have been caught earlier — update the `code-review` checklist, the `.agents/rules/` rules, alerts | checklist changes |

The postmortem is blameless: the focus is on the system, not on the agent/person.
