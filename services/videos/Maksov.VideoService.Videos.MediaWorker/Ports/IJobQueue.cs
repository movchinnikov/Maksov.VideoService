using Maksov.VideoService.Videos.Domain;

namespace Maksov.VideoService.Videos.MediaWorker.Ports;

/// <summary>
/// Transcode job queue. Lessons 1–3: Postgres (SKIP LOCKED + LISTEN/NOTIFY), lesson 4+: Kafka.
/// The lease protects against losing a job when a worker crashes: another worker picks up an expired job.
/// </summary>
public interface IJobQueue
{
    Task<IJobSignal> ListenAsync(CancellationToken cancellationToken);

    Task<TranscodeJob?> TryDequeueAsync(TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Extends the lease. <c>false</c> — another worker took the job, abandon the work.</summary>
    Task<bool> ExtendLeaseAsync(TranscodeJob job, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Moves the video to processing and returns the source key; <c>null</c> — the video is already processed or deleted.</summary>
    Task<string?> StartProcessingAsync(TranscodeJob job, CancellationToken cancellationToken);

    /// <summary>
    /// The video becomes ready and events are written to the outbox in the same transaction. <c>false</c> — the video can no longer be
    /// published (it was deleted during processing): the result in storage must be removed.
    /// </summary>
    Task<bool> CompleteAsync(TranscodeJob job, ProcessedVideo result, CancellationToken cancellationToken);

    Task SkipAsync(TranscodeJob job, CancellationToken cancellationToken);

    Task RetryAsync(TranscodeJob job, string details, TimeSpan delay, CancellationToken cancellationToken);

    Task FailAsync(TranscodeJob job, string reasonCode, string? details, CancellationToken cancellationToken);

    /// <summary>Returns the job to the queue without spending an attempt (normal worker shutdown).</summary>
    Task ReleaseAsync(TranscodeJob job, CancellationToken cancellationToken);
}

public interface IJobSignal : IAsyncDisposable
{
    /// <summary>Waits for a new-job notification or a timeout.</summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record TranscodeJob(Guid Id, Guid VideoId, int Attempt, int MaxAttempts, string? TraceParent);

public sealed record ProcessedVideo(int DurationMs, int Width, int Height, IReadOnlyList<Rendition> Renditions);
