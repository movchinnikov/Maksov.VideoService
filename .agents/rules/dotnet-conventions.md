---
trigger: glob
globs: "**/*.cs,**/*.csproj,**/*.sln,**/*.slnx,**/Directory.*.props,**/global.json"
---

# .NET conventions

## Version
- Always the **latest stable .NET** (currently `net10.0`). The version is set in one place: `Directory.Build.props` (`TargetFramework`) and `global.json` (`rollForward: latestFeature`). A major version upgrade is a separate PR with a breaking-changes review.
- `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended` — in `Directory.Build.props`.
- Package versions are centralized in `Directory.Packages.props` (Central Package Management).

## Saving resources and computation
- Streaming instead of buffering: `Stream`/`PipeReader` to MinIO/the client, no `ToArray()`/`ReadAsStringAsync()` for large data.
- Minimal allocations on the hot path: `System.Text.Json` source generators (`JsonSerializerContext`), `Span<T>`/`ArrayPool<T>` where measurable, `static` lambdas, no LINQ in hot loops.
- Do not touch Server GC + `DOTNET_GCHeapHardLimit`/`GCHeapAffinitizeMask` without measurements; in containers set `DOTNET_gcServer=0` for small workers (saves memory); `TieredPGO` is on by default.
- Pools: `NpgsqlDataSource` (one per application), `IHttpClientFactory`, one `IAmazonS3`/`IMinioClient` per application.
- Limit parallelism (`SemaphoreSlim`, `Channel<T>` with bounded capacity, `Parallel.ForEachAsync` with `MaxDegreeOfParallelism`) — never "start everything at once".
- Polling only with backoff and/or `LISTEN/NOTIFY`; empty loops are forbidden.
- Heavy CPU tasks (ffmpeg) run in an external process with limits, not on the ASP.NET thread pool.
- Consider Native AOT / trimming for small workers and CLIs (if dependencies are compatible); `InvariantGlobalization` where culture is not needed.

## Asynchrony — only where needed
- `async` only for I/O (network, disk, DB, processes). CPU-bound code is synchronous; do not wrap it in `Task.Run` in ASP.NET.
- No `.Result`, `.Wait()`, `async void` (except UI event handlers).
- `CancellationToken` is passed everywhere; timeouts via `CancellationTokenSource.CancelAfter` / Polly.
- `ValueTask` only for hot paths that often complete synchronously.
- `ConfigureAwait(false)` in `libs/*` libraries; not needed in ASP.NET applications.
- `IAsyncEnumerable<T>` for streaming large lists.

## Other
- Configuration via `IOptions<T>` + `ValidateDataAnnotations().ValidateOnStart()`.
- External calls go through `Microsoft.Extensions.Http.Resilience` (Polly v8): timeout, retry with jitter, circuit breaker. After N attempts — to the DLQ / status `Failed`.
- OpenTelemetry: traces, metrics, logs; `trace_id` is propagated into events and jobs.
- Migrations are a separate step (`migrator`), not run at application startup.
- `IAsyncDisposable` for resources; temporary files are deleted in `finally`.
