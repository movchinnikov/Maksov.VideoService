---
name: wf-embedding-model-change
description: >-
  Changing the embedding model without downtime: bump model_version, background recomputation into a new
  Qdrant collection, metric comparison, alias switch, deletion of the old collection after
  observation. Use when replacing/fine-tuning any embedding model.
---

# Workflow: embedding model change

| # | Agent | Action | Artifact / gate |
| --- | --- | --- | --- |
| 1 | `architect` | A short ADR: why the new model, the expected gain, the cost of recomputation on a local machine | ADR `Accepted` by the user |
| 2 | `ml-engineer` | Bump `model_version` (`<family>-<size>-v<N>`), pin the weights and preprocessing | feature-worker config |
| 3 | `analyst` | If the dimension/set of vectors changes — a compatible change to `VideoEmbedded` | contract, compatibility check |
| 4 | `ml-engineer` | Create the collection `videos_v{N}`; new videos are written **to both** collections (dual write) | both collections grow |
| 5 | `data-engineer` + `ml-engineer` | Background recomputation of all videos (Spark/batch job), progress checkpoints, resource limit | 100 % of videos in `videos_v{N}` |
| 6 | `ml-engineer` | Comparison: recall@k, nDCG@k, stability across resolutions, inference latency | `docs/ml/<model_version>.md`, new ≥ old |
| 7 | `reviewer` | Review of the procedure and the report | no `BLOCKER` |
| 8 | `ml-engineer` | Switch the alias `videos` → `videos_v{N}` (atomically) | Feed reads the new version |
| 9 | `devops` | Observation (feed metrics, errors) over a period (e.g., 7 days) | dashboard |
| 10 | `ml-engineer` | Stop dual write, delete `videos_v{N-1}` | ADR → "done" |

**Rollback:** switch the alias back to `videos_v{N-1}` (that is why the old collection lives until the end of step 9).
