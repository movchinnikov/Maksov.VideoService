using Maksov.VideoService.Analysis.Dal;
using Maksov.VideoService.Analysis.Worker;
using Maksov.VideoService.Analysis.Worker.Adapters.Ffmpeg;
using Maksov.VideoService.Analysis.Worker.Adapters.Ollama;
using Maksov.VideoService.Analysis.Worker.Features.Analyze;
using Maksov.VideoService.Analysis.Worker.Ports;
using Maksov.VideoService.BuildingBlocks.Observability;
using Maksov.VideoService.BuildingBlocks.Postgres;
using Maksov.VideoService.BuildingBlocks.Storage;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceTelemetry("videoservice-analysis-worker");

builder.Services.AddOptions<AnalysisOptions>()
    .Bind(builder.Configuration.GetSection(AnalysisOptions.SectionName))
    .Validate(static o => o.MaxConcurrentJobs is >= 1 and <= 4, "Analysis:MaxConcurrentJobs must be 1..4")
    .Validate(static o => o.LeaseSeconds >= 60, "Analysis:LeaseSeconds must be >= 60")
    .Validate(static o => !string.IsNullOrWhiteSpace(o.WorkDirectory), "Analysis:WorkDirectory is required")
    .ValidateOnStart();

builder.Services.Configure<HostOptions>(static o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

// Adapters
builder.Services.AddPostgres(builder.Configuration, "videoservice-analysis-worker");
builder.Services.AddS3ObjectStorage(builder.Configuration);
builder.Services.AddDal();
builder.Services.AddSingleton<IVideoInspector, FfmpegVideoInspector>();

var captioner = builder.Configuration.GetSection(AnalysisOptions.SectionName).Get<AnalysisOptions>()?.Captioner ?? new CaptionerOptions();
if (string.Equals(captioner.Provider, "ollama", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<OllamaClient>(client =>
    {
        client.BaseAddress = new Uri(captioner.Endpoint);
        client.Timeout = TimeSpan.FromSeconds(captioner.TimeoutSeconds);
    });
    builder.Services.AddSingleton<IFrameCaptioner, OllamaFrameCaptioner>();
}
else
{
    builder.Services.AddSingleton<IFrameCaptioner, NullFrameCaptioner>();
}

// "Analyze" slice
builder.Services.AddSingleton<Heartbeat>();
builder.Services.AddSingleton<AnalyzeHandler>();
builder.Services.AddHostedService<AnalyzeWorker>();

await builder.Build().RunAsync();
