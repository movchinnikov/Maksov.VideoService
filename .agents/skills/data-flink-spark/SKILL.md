---
name: data-flink-spark
description: >-
  Stream and batch processing: Flink (windows, state, watermark, exactly-once, checkpoints),
  Spark (batch, ALS), bronze/silver/gold lake on MinIO + Iceberg/Parquet, Airflow/Dagster,
  data quality (Great Expectations/Soda), late and duplicate events.
---

# Data: Flink and Spark

## Input / output
- **Input:** Kafka topics per `contracts/events/`.
- **Output:** Flink jobs (`data/flink-jobs`), Spark jobs (`data/spark-jobs`), DAGs (`data/dags`), marts in ClickHouse/Iceberg, aggregates in the hot store.

## Principles
- **Event time**, not processing time: watermark with a lateness tolerance (for example, 30 s), allowed lateness + side output for late events.
- **Deduplication** by `event_id` in state with a TTL.
- **Exactly-once:** checkpoints in MinIO (`s3://flink/checkpoints`), transactional Kafka sink / idempotent upsert in the sink.
- **Lake layers:** bronze (raw events, Iceberg, partitioned by date) → silver (cleaned, deduplicated, typed) → gold (marts: trends, watch ratios, ML datasets).
- **Data quality:** checks on silver/gold (non-null keys, uniqueness, watch-ratio ranges 0..1, freshness) — a failure blocks the DAG.
- **Resources:** parallelism and TaskManager/executor memory are set explicitly and kept small for a local machine.

## Testing
- Flink: `MiniClusterWithClientResource` / operator tests with a harness, scenarios with lateness and duplicates.
- Spark: a local `SparkSession` on small fixtures, comparison against a reference.
- End to end: Testcontainers (Kafka) + the job + mart verification.

## Done when
- [ ] The job is reproducible (same input → same output), restart from a checkpoint without duplicates.
- [ ] Tests on test streams, including late and repeated events.
- [ ] Consumer lag is monitored (metric + Grafana panel, request to devops).
- [ ] The mart schema is documented.
