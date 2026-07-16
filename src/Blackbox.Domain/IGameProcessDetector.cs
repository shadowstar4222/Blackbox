namespace Blackbox.Domain;

public interface IGameProcessDetector
{
    Task<GameCaptureTarget?> DetectAsync(CancellationToken cancellationToken = default);
}
