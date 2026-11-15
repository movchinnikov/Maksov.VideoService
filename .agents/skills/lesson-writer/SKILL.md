---
name: lesson-writer
description: >-
  Writing a lesson of the series: goal and outcome, step-by-step walkthrough, common mistakes, self-check
  tasks, README with run instructions, git tag lesson-NN. Use as the last step of a feature/lesson cycle.
---

# Lesson Writer

## Input / output
- **Input:** ADRs, contracts, `git diff lesson-(NN-1)..HEAD`, `make demo` output.
- **Output:** `docs/lessons/NN-<slug>.md` following the [lesson-template.md](resources/lesson-template.md) template, an updated `README.md`, the `lesson-NN` tag.

## Steps
1. Gather material: what changed (diff), why (ADR), how to verify (`make demo`).
2. Write the lesson from the template; code in the lesson — as file links and short excerpts, not a copy of everything.
3. Common mistakes — from real problems found by the reviewer/QA in this cycle.
4. Self-check tasks: 3–5, at least one with a "star".
5. **Reproducibility check:** a clean clone into a temporary folder → `git checkout lesson-NN` → follow the lesson steps literally → `make demo` is green.
6. Update the README (lessons table, run commands).
7. `git tag -a lesson-NN -m "Lesson NN: <title>"` (push — only on the user's request).

## Done when
- [ ] The lesson steps are reproduced from a clean clone.
- [ ] There are a goal, outcome, walkthrough, mistakes, tasks.
- [ ] The README is updated, the tag is set.
