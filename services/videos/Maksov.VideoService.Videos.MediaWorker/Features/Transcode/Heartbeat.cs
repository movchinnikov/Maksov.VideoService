namespace Maksov.VideoService.Videos.MediaWorker.Features.Transcode;

/// <summary>Heartbeat for the container healthcheck: updated by the polling loop and by lease extension during a long job.</summary>
internal sealed class Heartbeat(Microsoft.Extensions.Options.IOptions<TranscodeOptions> options)
{
    private readonly string _path = options.Value.HeartbeatFile;

    public void Beat()
    {
        try
        {
            File.WriteAllText(_path, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
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

