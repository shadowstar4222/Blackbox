using Blackbox.Export;

namespace Blackbox.Tests;

public sealed class BundledFfmpegProvisionerTests
{
    [Fact]
    public async Task Complete_portable_installation_is_available_without_downloading()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        try
        {
            foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
            {
                File.WriteAllText(Path.Combine(root, "bin", name), name);
            }

            var provisioner = new BundledFfmpegProvisioner(new FfmpegOptions
            {
                RootDirectory = root,
                WorkDirectory = Path.Combine(root, "work")
            });
            var installed = await provisioner.EnsureInstalledAsync();
            Assert.Equal(Path.Combine(root, "bin", "ffmpeg.exe"), installed.FfmpegPath);
            Assert.Equal(installed, await provisioner.EnsureInstalledAsync());

            File.Delete(installed.FfprobePath);
            var error = await Assert.ThrowsAsync<FileNotFoundException>(() => provisioner.EnsureInstalledAsync());
            Assert.Equal(installed.FfprobePath, error.FileName);
            Assert.Contains("ffprobe.exe", error.Message);
            Assert.True(File.Exists(installed.FfmpegPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Cancellation_is_honored_before_checking_the_installation()
    {
        var provisioner = new BundledFfmpegProvisioner(new FfmpegOptions
        {
            RootDirectory = "missing",
            WorkDirectory = "unused"
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provisioner.EnsureInstalledAsync(cancellationToken: cancellation.Token));
    }
}
