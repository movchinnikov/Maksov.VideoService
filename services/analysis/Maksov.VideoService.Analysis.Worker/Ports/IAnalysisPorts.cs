using Maksov.VideoService.Analysis.Bll.Ports;
using Maksov.VideoService.Analysis.Worker.Domain;

namespace Maksov.VideoService.Analysis.Worker.Ports;

/// <summary>Analysis tools: "readings" for a video file.</summary>
public interface IVideoInspector
{
    Task<VideoFacts> InspectAsync(string filePath, AnalysisJob job, CancellationToken cancellationToken);
}

/// <summary>Caption of the frame content (vision model). May return <c>null</c> — then the description is built from the "readings".</summary>
public interface IFrameCaptioner
{
    Task<string?> CaptionAsync(string filePath, AnalysisJob job, CancellationToken cancellationToken);
}
