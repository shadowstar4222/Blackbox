namespace Blackbox.Export;

public interface IFfmpegProvisioner
{
    Task<FfmpegInstallation> EnsureInstalledAsync(
        IProgress<FfmpegProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
