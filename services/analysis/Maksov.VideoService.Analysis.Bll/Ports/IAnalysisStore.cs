namespace Maksov.VideoService.Analysis.Bll.Ports;

public enum AnalysisStatus
{
    Queued,
    Processing,
    Done,
    Failed,
}

/// <summary>Analysis request: arrives with the "video processed" event.</summary>
public sealed record AnalysisRequest(Guid EventId, Guid VideoId, string OwnerId, string SourceKey, int DurationMs, int Width, int Height);

public sealed record AnalysisRecord(Guid VideoId, string OwnerId, AnalysisStatus Status, string? Description, IReadOnlyList<string> Tags, DateTimeOffset UpdatedAt);

/// <summary>Analysis store port for the API.</summary>
public interface IAnalysisRepository
{
    /// <summary>Idempotently enqueues an analysis: a repeated event for the same video is a no-op (<c>false</c>).</summary>
    Task<bool> EnqueueAsync(AnalysisRequest request, CancellationToken cancellationToken);

    /// <summary>Video analysis (with owner — the caller checks permissions); none — <c>null</c>.</summary>
    Task<AnalysisRecord?> GetAsync(Guid videoId, CancellationToken cancellationToken);

    /// <summary>Deletes a video analysis ("video deleted" event). Idempotent: <c>false</c> — the analysis is already gone.</summary>
    Task<bool> DeleteAsync(Guid videoId, CancellationToken cancellationToken);
}

public sealed record AnalysisJob(Guid VideoId, string SourceKey, int DurationMs, int Width, int Height, int Attempt, int MaxAttempts);

public sealed record AnalysisResult(string Description, IReadOnlyList<string> Tags, string FactsJson);

public interface IJobSignal : IAsyncDisposable
{
    /// <summary>Waits for a new-job notification or a timeout.</summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Analysis job queue port for the worker. Postgres (SKIP LOCKED + lease + LISTEN/NOTIFY); in lesson 4 — Kafka.
/// </summary>
public interface IAnalysisQueue
{
    Task<IJobSignal> ListenAsync(CancellationToken cancellationToken);

    Task<AnalysisJob?> TryDequeueAsync(TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Renews the lease. <c>false</c> — another worker took the job, the work must be dropped.</summary>
    Task<bool> ExtendLeaseAsync(AnalysisJob job, TimeSpan lease, CancellationToken cancellationToken);

    Task CompleteAsync(AnalysisJob job, AnalysisResult result, CancellationToken cancellationToken);

    Task RetryAsync(AnalysisJob job, string details, TimeSpan delay, CancellationToken cancellationToken);

    Task FailAsync(AnalysisJob job, string reasonCode, string? details, CancellationToken cancellationToken);

    /// <summary>Returns a job to the queue without spending an attempt (graceful worker stop).</summary>
    Task ReleaseAsync(AnalysisJob job, CancellationToken cancellationToken);
}
