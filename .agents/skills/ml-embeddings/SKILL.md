---
name: ml-embeddings
description: >-
  Multimodal embeddings and recommendations: CLIP/SigLIP on keyframes, CLAP/MERT and
  Whisper for audio, bge-m3/multilingual-e5 for text and hashtags, modality fusion,
  Qdrant (HNSW, filters), candidates, ALS/two-tower, ranking, offline metrics.
  Use for the Feature Worker, recommendations and quality evaluation.
---

# ML: embeddings and recommendations

## Input / output
- **Input:** `VideoProcessed`; frames/audio/metadata in MinIO.
- **Output:** points in Qdrant (`videos_v{N}`, alias `videos`), `VideoEmbedded{video_id, model_version, vector_refs}`, report `docs/ml/<model_version>.md`.

## Feature pipeline
1. **Visual:** scene detection (PySceneDetect) → keyframes (≤ 16) → CLIP/SigLIP → mean/attention pooling. Actions — VideoMAE/X-CLIP (advanced lesson).
2. **Audio:** CLAP (general sound) or MERT (music); Chromaprint for the track fingerprint; Whisper (small, CPU) → transcript into the text modality.
3. **Text/hashtags:** bge-m3 or multilingual-e5 on the description + transcript; hashtags — embeddings by co-occurrence (word2vec/PPMI+SVD).
4. **Fusion:** L2 normalization of each modality → weighted concatenation + projection (PCA/small MLP); weights tuned by recall@k.
5. **Storage:** named vectors in Qdrant (`visual`, `audio`, `text`, `fused`), payload: `model_version`, `lang`, `region`, `age_rating`, `created_at`.

## Recommendations
- Cold start: content similarity on `fused` + payload filters.
- Behavior: ALS (Spark MLlib) on `VideoViewed` weighted by watch ratio; two-tower (PyTorch) as a follow-up.
- Candidates from several sources (content, ALS, Flink trends, follows) → ranking (LightGBM/logistic regression) → blending in Feed.

## Inference
Python FastAPI in a container, CPU by default (small/base models), batching; ONNX Runtime from .NET is an advanced option. Models are cached in a volume, versions are pinned.

## Evaluation
recall@k / nDCG@k on a time-based offline split; labeled "similar/not similar" pairs; **stability test:** cosine between embeddings of the same video at 360p and 1080p ≥ a threshold (for example, 0.95); A/B simulation (interleaving on logs).

## Done when
- [ ] Every vector has a `model_version`.
- [ ] There is a recomputation procedure (`wf-embedding-model-change`).
- [ ] Metrics are recorded in `docs/ml/`.
- [ ] The resolution stability test is green.
- [ ] The `VideoProcessed` handler is idempotent (upsert by `video_id`).

## Current layout (after the move to services)
- **Frame captioning with a vision model (VLM)** is an open **base adapter** in `services/analysis` (`Analysis.Worker/Adapters/Ollama/OllamaCaptioner.cs`): local Ollama, default model `moondream`, compose profile `ml`, enabled with `Analysis__Captioner__Provider=ollama` (`make up-ml`), off by default. Without it the description is ffmpeg "instrument readings"; semantics (what is depicted) appear only with a model. A model failure does not block the description.
- **Proprietary stays in `private/`:** custom prompts, model selection and fine-tuning, weights, datasets, ranking. The public code has only the `IFrameCaptioner` port and an open implementation with a neutral default prompt; private implementations are plugged in via configuration and are not committed.
- VLM results carry an explicit model version (`model_version`) when moving to embeddings (lesson 8); changing the model — `wf-embedding-model-change`.
- "Interesting videos" (tab/screen and recommendations) are **deferred**: lesson 1 has neither API nor UI; they will appear after embeddings (lesson 8) and recommendations (lesson 9). The lesson 1 feed is follows + own videos, no ranking.