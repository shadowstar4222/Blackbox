namespace Blackbox.Domain;

public interface IMediaProbe
{
    Task<MediaFileProbeResult> ProbeAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
