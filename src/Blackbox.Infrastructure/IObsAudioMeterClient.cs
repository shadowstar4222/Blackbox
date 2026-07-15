using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public interface IObsAudioMeterClient
{
    Task<IReadOnlyList<AudioLevelSnapshot>> CaptureInputLevelsAsync(
        ObsConnectionSettings settings,
        string inputName,
        TimeSpan duration,
        IProgress<AudioLevelSnapshot>? progress = null,
        CancellationToken cancellationToken = default);
}
