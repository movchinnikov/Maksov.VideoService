---
trigger: always_on
---

# Local-only, configuration, resources

- Dependencies on cloud SaaS/PaaS (AWS, GCP, Azure, managed Kafka, etc.) are forbidden. Replacements: S3→MinIO, MSK→Redpanda/Strimzi, RDS→PostgreSQL, Snowflake→ClickHouse/DuckDB, EKS→kind/k3d.
- Secrets are not committed. Any new environment variable is added to `.env.example` with a safe default and a comment.
- Every container in compose/Helm has explicit `cpus`/`mem_limit` (or `resources.limits`/`requests`) and a healthcheck.
- Images are pinned to a version (not `latest`).
- A new service must come up via `make up` and be verified via `make demo`.
