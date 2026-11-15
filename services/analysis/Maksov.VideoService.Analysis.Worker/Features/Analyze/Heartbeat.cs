using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Analysis.Worker.Features.Analyze;

/// <summary>Container healthcheck heartbeat: refreshed by the queue loop and lease renewal during a long job.</summary>
internal sealed class Heartbeat(IOptions<AnalysisOptions> options)
{
    private readonly string _path = options.Value.HeartbeatFile;

    public void Beat()
    {
        try
        {
            File.WriteAllText(_path, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (IOException)
        {
            // The heartbeat is not critical for processing.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
