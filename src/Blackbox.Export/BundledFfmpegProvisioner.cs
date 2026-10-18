namespace Blackbox.Export;

public sealed class BundledFfmpegProvisioner(FfmpegOptions options) : IFfmpegProvisioner
{
    public Task<FfmpegInstallation> EnsureInstalledAsync(
        IProgress<FfmpegProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bin = Path.Combine(options.RootDirectory, "bin");
        var ffmpeg = Path.Combine(bin, "ffmpeg.exe");
        var ffprobe = Path.Combine(bin, "ffprobe.exe");
        var missing = new[] { ffmpeg, ffprobe }.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
        {
            throw new FileNotFoundException(
                $"Bundled video tools are missing: {string.Join(", ", missing.Select(Path.GetFileName))}. " +
                $"Repair the Blackbox installation at {bin}. No download was attempted.", missing[0]);
        }

        return Task.FromResult(new FfmpegInstallation(options.RootDirectory, ffmpeg, ffprobe));
    }
}
