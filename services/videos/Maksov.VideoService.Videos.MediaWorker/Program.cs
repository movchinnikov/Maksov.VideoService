using Maksov.VideoService.BuildingBlocks.Observability;
using Maksov.VideoService.BuildingBlocks.Postgres;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Videos.MediaWorker;
using Maksov.VideoService.Videos.MediaWorker.Adapters.Ffmpeg;
using Maksov.VideoService.Videos.MediaWorker.Adapters.Http;
using Maksov.VideoService.Videos.MediaWorker.Adapters.Postgres;
using Maksov.VideoService.Videos.MediaWorker.Features.Outbox;
using Maksov.VideoService.Videos.MediaWorker.Features.Transcode;
using Maksov.VideoService.Videos.MediaWorker.Ports;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceTelemetry(WorkerMetrics.ServiceName);

builder.Services.AddOptions<TranscodeOptions>()
    .Bind(builder.Configuration.GetSection(TranscodeOptions.SectionName))
    .Validate(static o => o.MaxConcurrentJobs is >= 1 and <= 8, "Transcode:MaxConcurrentJobs must be 1..8")
    .Validate(static o => o.Threads is >= 1 and <= 32, "Transcode:Threads must be 1..32")
    .Validate(static o => o.LeaseSeconds >= 30, "Transcode:LeaseSeconds must be >= 30")
    .Validate(static o => !string.IsNullOrWhiteSpace(o.WorkDirectory), "Transcode:WorkDirectory is required")
    .ValidateOnStart();

builder.Services.Configure<HostOptions>(static o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

// Adapters
builder.Services.AddPostgres(builder.Configuration, "videoservice-media-worker");
builder.Services.AddS3ObjectStorage(builder.Configuration);
builder.Services.AddSingleton<IJobQueue, PostgresJobQueue>();
builder.Services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
builder.Services.AddSingleton<ITranscoder, FfmpegTranscoder>();

// Slice "Transcoding"
builder.Services.AddSingleton<WorkerMetrics>();
builder.Services.AddSingleton<Heartbeat>();
builder.Services.AddSingleton<TranscodeHandler>();
builder.Services.AddHostedService<TranscodeWorker>();

// Slice "Event delivery": only if at least one subscriber (analysis, comments) is configured. Without them the lesson works as before.
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection(OutboxOptions.SectionName));
builder.Services.Configure<InternalOptions>(builder.Configuration.GetSection(InternalOptions.SectionName));
var outbox = builder.Configuration.GetSection(OutboxOptions.SectionName).Get<OutboxOptions>() ?? new OutboxOptions();
if (outbox.ConfiguredSubscribers().Count > 0)
{
    builder.Services.AddSingleton<IOutbox, PostgresOutbox>();
    builder.Services.AddHttpClient("outbox", client => client.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddSingleton<IEventPublisher, HttpEventPublisher>();
    builder.Services.AddHostedService<OutboxDispatcher>();
}

await builder.Build().RunAsync();
