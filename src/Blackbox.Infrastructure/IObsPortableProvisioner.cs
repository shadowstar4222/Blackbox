using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public interface IObsPortableProvisioner
{
    Task<ObsInstallation> EnsureInstalledAsync(
        IProgress<ObsSetupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task LaunchAsync(
        ObsInstallation installation,
        ObsConnectionSettings connectionSettings,
        CancellationToken cancellationToken = default);
}
