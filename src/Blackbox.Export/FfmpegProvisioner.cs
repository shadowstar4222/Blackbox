using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Blackbox.Export;

public sealed class FfmpegProvisioner(
    HttpClient httpClient,
    FfmpegOptions options,
    ILogger<FfmpegProvisioner> logger) : IFfmpegProvisioner, IDisposable
{
    private readonly SemaphoreSlim _installationGate = new(1, 1);

    public async Task<FfmpegInstallation> EnsureInstalledAsync(
        IProgress<FfmpegProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installed = FindInstallation();
        if (installed is not null)
        {
            return installed;
        }

        await _installationGate.WaitAsync(cancellationToken);
        try
        {
            installed = FindInstallation();
            if (installed is not null)
            {
                return installed;
            }

            EnsureHttps(options.PackageUri, nameof(options.PackageUri));
            EnsureHttps(options.ChecksumUri, nameof(options.ChecksumUri));
            var parentDirectory = Directory.GetParent(options.RootDirectory)?.FullName
                ?? throw new InvalidOperationException("The FFmpeg directory has no parent directory.");
            Directory.CreateDirectory(parentDirectory);
            var operationId = Guid.NewGuid().ToString("N");
            var archivePath = Path.Combine(parentDirectory, $"ffmpeg-{operationId}.zip");
            var stagingDirectory = Path.Combine(parentDirectory, $"ffmpeg-staging-{operationId}");
            try
            {
                progress?.Report(new FfmpegProvisionProgress("Downloading the Blackbox video tools...", 0));
                await DownloadAsync(archivePath, progress, cancellationToken);
                progress?.Report(new FfmpegProvisionProgress("Verifying the video tools...", 96));
                await VerifyChecksumAsync(archivePath, cancellationToken);
                progress?.Report(new FfmpegProvisionProgress("Installing the video tools...", 98));
                ZipFile.ExtractToDirectory(archivePath, stagingDirectory);

                var stagedFfmpeg = Directory
                    .EnumerateFiles(stagingDirectory, "ffmpeg.exe", SearchOption.AllDirectories)
                    .FirstOrDefault()
                    ?? throw new InvalidDataException("The FFmpeg package does not contain ffmpeg.exe.");
                var packageRoot = Directory.GetParent(stagedFfmpeg)?.Parent?.FullName
                    ?? throw new InvalidDataException("The FFmpeg package has an unexpected layout.");
                if (Directory.Exists(options.RootDirectory))
                {
                    Directory.Delete(options.RootDirectory, true);
                }

                Directory.Move(packageRoot, options.RootDirectory);
                installed = FindInstallation()
                    ?? throw new InvalidDataException("The FFmpeg package is incomplete after extraction.");
                progress?.Report(new FfmpegProvisionProgress("Video tools are ready.", 100));
                logger.LogInformation("Provisioned FFmpeg at {FfmpegRootDirectory}.", options.RootDirectory);
                return installed;
            }
            finally
            {
                TryDeleteFile(archivePath);
                TryDeleteDirectory(stagingDirectory);
            }
        }
        finally
        {
            _installationGate.Release();
        }
    }

    private FfmpegInstallation? FindInstallation()
    {
        var binDirectory = Path.Combine(options.RootDirectory, "bin");
        var ffmpegPath = Path.Combine(binDirectory, "ffmpeg.exe");
        var ffprobePath = Path.Combine(binDirectory, "ffprobe.exe");
        var ffplayPath = Path.Combine(binDirectory, "ffplay.exe");
        return File.Exists(ffmpegPath) && File.Exists(ffprobePath) && File.Exists(ffplayPath)
            ? new FfmpegInstallation(options.RootDirectory, ffmpegPath, ffprobePath, ffplayPath)
            : null;
    }

    private async Task DownloadAsync(
        string destinationPath,
        IProgress<FfmpegProvisionProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            options.PackageUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long copied = 0;
        var lastReportedPercent = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            if (totalBytes is > 0)
            {
                var percent = Math.Min(95, (int)(copied * 95d / totalBytes.Value));
                if (percent > lastReportedPercent)
                {
                    lastReportedPercent = percent;
                    progress?.Report(new FfmpegProvisionProgress(
                        "Downloading the Blackbox video tools...",
                        percent));
                }
            }
        }
    }

    private async Task VerifyChecksumAsync(string archivePath, CancellationToken cancellationToken)
    {
        var expectedText = await httpClient.GetStringAsync(options.ChecksumUri, cancellationToken);
        var expected = expectedText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (expected is null || expected.Length != 64)
        {
            throw new InvalidDataException("The FFmpeg checksum response is invalid.");
        }

        await using var archive = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(archive, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The FFmpeg package checksum did not match the published checksum.");
        }
    }

    private static void EnsureHttps(Uri uri, string parameterName)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{parameterName} must use HTTPS.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
        }
    }

    public void Dispose() => _installationGate.Dispose();
}
