using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed class ObsPortableProvisioner(
    HttpClient httpClient,
    ObsProvisioningOptions options,
    IObsInstallationLocator installationLocator,
    ILogger<ObsPortableProvisioner> logger) : IObsPortableProvisioner, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _installationGate = new(1, 1);

    public async Task<ObsInstallation> EnsureInstalledAsync(
        IProgress<ObsSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _installationGate.WaitAsync(cancellationToken);
        try
        {
            return await EnsureInstalledCoreAsync(progress, cancellationToken);
        }
        finally
        {
            _installationGate.Release();
        }
    }

    private async Task<ObsInstallation> EnsureInstalledCoreAsync(
        IProgress<ObsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var executablePath = GetExecutablePath(options.PortableRootDirectory);
        progress?.Report(new ObsSetupProgress(
            ObsSetupStage.CheckingInstallation,
            "Checking the Blackbox OBS installation..."));

        if (File.Exists(executablePath))
        {
            PreparePortableConfiguration(options.PortableRootDirectory);
            return new ObsInstallation(options.PortableRootDirectory, executablePath, "installed");
        }

        var existingInstallation = installationLocator.FindExistingInstallation();
        if (existingInstallation is not null)
        {
            return await CloneExistingInstallationAsync(existingInstallation, progress, cancellationToken);
        }

        var release = await GetLatestReleaseAsync(cancellationToken);
        var asset = release.Assets.FirstOrDefault(static candidate =>
            candidate.Name.EndsWith("Windows-x64.zip", StringComparison.OrdinalIgnoreCase) &&
            !candidate.Name.Contains("PDB", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The latest OBS release does not contain a Windows x64 portable package.");

        if (asset.DownloadUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("OBS package download must use HTTPS.");
        }

        var parentDirectory = Directory.GetParent(options.PortableRootDirectory)?.FullName
            ?? throw new InvalidOperationException("The OBS portable directory has no parent directory.");
        Directory.CreateDirectory(parentDirectory);

        var operationId = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(parentDirectory, $"obs-{operationId}.zip");
        var stagingDirectory = Path.Combine(parentDirectory, $"obs-staging-{operationId}");

        try
        {
            await DownloadAsync(asset, archivePath, progress, cancellationToken);
            VerifyDigest(asset, archivePath, progress);

            progress?.Report(new ObsSetupProgress(ObsSetupStage.Extracting, "Extracting the private OBS copy..."));
            ZipFile.ExtractToDirectory(archivePath, stagingDirectory);
            var stagedExecutable = Directory
                .EnumerateFiles(stagingDirectory, "obs64.exe", SearchOption.AllDirectories)
                .FirstOrDefault(static path => path.EndsWith(
                    Path.Combine("bin", "64bit", "obs64.exe"),
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The OBS archive did not contain bin\\64bit\\obs64.exe.");

            var stagedRoot = Directory.GetParent(stagedExecutable)?.Parent?.Parent?.FullName
                ?? throw new InvalidDataException("The OBS archive has an unexpected directory layout.");
            Directory.Move(stagedRoot, options.PortableRootDirectory);
            PreparePortableConfiguration(options.PortableRootDirectory);

            logger.LogInformation(
                "Provisioned portable OBS {Version} at {PortableRootDirectory}.",
                release.Version,
                options.PortableRootDirectory);
            return new ObsInstallation(
                options.PortableRootDirectory,
                GetExecutablePath(options.PortableRootDirectory),
                release.Version);
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public Task LaunchAsync(
        ObsInstallation installation,
        ObsConnectionSettings connectionSettings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        connectionSettings.Validate();
        if (!File.Exists(installation.ExecutablePath))
        {
            throw new FileNotFoundException("The Blackbox OBS executable is missing.", installation.ExecutablePath);
        }

        ObsWebSocketConfigurationWriter.Write(installation.RootDirectory, connectionSettings);
        var startInfo = new ProcessStartInfo
        {
            FileName = installation.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(installation.ExecutablePath)!,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--portable");
        startInfo.ArgumentList.Add("--multi");
        startInfo.ArgumentList.Add("--minimize-to-tray");
        startInfo.ArgumentList.Add("--disable-updater");
        startInfo.ArgumentList.Add("--disable-missing-files-check");
        startInfo.ArgumentList.Add("--websocket_ipv4_only");
        startInfo.ArgumentList.Add("--websocket_port");
        startInfo.ArgumentList.Add(connectionSettings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows could not start the Blackbox OBS process.");
        logger.LogInformation(
            "Launched portable OBS {Version} from {ExecutablePath}.",
            installation.Version,
            installation.ExecutablePath);
        return Task.CompletedTask;
    }

    private async Task<ObsRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.LatestReleaseApiUri);
        request.Headers.UserAgent.ParseAdd("Blackbox/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<ObsRelease>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("GitHub returned an empty OBS release response.");
    }

    private async Task<ObsInstallation> CloneExistingInstallationAsync(
        ObsInstallation existingInstallation,
        IProgress<ObsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var parentDirectory = Directory.GetParent(options.PortableRootDirectory)?.FullName
            ?? throw new InvalidOperationException("The OBS portable directory has no parent directory.");
        Directory.CreateDirectory(parentDirectory);
        var stagingDirectory = Path.Combine(parentDirectory, $"obs-staging-{Guid.NewGuid():N}");

        try
        {
            progress?.Report(new ObsSetupProgress(
                ObsSetupStage.CopyingExistingInstallation,
                "Found OBS Studio. Preparing Blackbox from the local installation...",
                0));
            var files = Directory
                .EnumerateFiles(existingInstallation.RootDirectory, "*", SearchOption.AllDirectories)
                .Where(path => ShouldCloneFile(existingInstallation.RootDirectory, path))
                .ToArray();
            var lastPercent = -1;
            for (var index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = files[index];
                var relativePath = Path.GetRelativePath(existingInstallation.RootDirectory, sourcePath);
                var destinationPath = Path.Combine(stagingDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                if (!TryCreateHardLink(destinationPath, sourcePath))
                {
                    await CopyFileAsync(sourcePath, destinationPath, cancellationToken);
                }

                var percent = files.Length == 0 ? 100 : (index + 1) * 100 / files.Length;
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(new ObsSetupProgress(
                        ObsSetupStage.CopyingExistingInstallation,
                        $"Preparing Blackbox from the local OBS installation... {percent}%",
                        percent));
                }
            }

            Directory.Move(stagingDirectory, options.PortableRootDirectory);
            PreparePortableConfiguration(options.PortableRootDirectory);
            logger.LogInformation(
                "Provisioned portable OBS {Version} from existing installation {ExistingRootDirectory}.",
                existingInstallation.Version,
                existingInstallation.RootDirectory);
            return new ObsInstallation(
                options.PortableRootDirectory,
                GetExecutablePath(options.PortableRootDirectory),
                existingInstallation.Version);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static bool ShouldCloneFile(string sourceRoot, string path)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, path);
        var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        var fileName = Path.GetFileName(relativePath);
        return !firstSegment.Equals("config", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("portable_mode", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("portable_mode.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateHardLink(string destinationPath, string sourcePath)
    {
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(
                Path.GetPathRoot(destinationPath),
                Path.GetPathRoot(sourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return CreateHardLink(destinationPath, sourcePath, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private async Task DownloadAsync(
        ObsReleaseAsset asset,
        string destinationPath,
        IProgress<ObsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ObsSetupProgress(ObsSetupStage.Downloading, "Downloading OBS Studio...", 0));
        using var response = await httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? asset.Size;

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            if (totalBytes > 0)
            {
                var percent = (int)Math.Clamp(written * 100 / totalBytes, 0, 100);
                progress?.Report(new ObsSetupProgress(ObsSetupStage.Downloading, $"Downloading OBS Studio... {percent}%", percent));
            }
        }
    }

    private static void VerifyDigest(
        ObsReleaseAsset asset,
        string archivePath,
        IProgress<ObsSetupProgress>? progress)
    {
        progress?.Report(new ObsSetupProgress(ObsSetupStage.Verifying, "Verifying the OBS download..."));
        if (string.IsNullOrWhiteSpace(asset.Digest))
        {
            return;
        }

        const string prefix = "sha256:";
        if (!asset.Digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The OBS release uses an unsupported package digest.");
        }

        using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        var expected = asset.Digest[prefix.Length..];
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded OBS package failed SHA-256 verification.");
        }
    }

    private static string GetExecutablePath(string rootDirectory) =>
        Path.Combine(rootDirectory, "bin", "64bit", "obs64.exe");

    private static void PreparePortableConfiguration(string rootDirectory)
    {
        File.WriteAllText(Path.Combine(rootDirectory, "portable_mode.txt"), string.Empty);
        var configurationDirectory = Path.Combine(rootDirectory, "config", "obs-studio");
        Directory.CreateDirectory(configurationDirectory);
        var userConfigurationPath = Path.Combine(configurationDirectory, "user.ini");
        if (!File.Exists(userConfigurationPath))
        {
            File.WriteAllText(
                userConfigurationPath,
                "[General]\r\nFirstRun=true\r\n\r\n[BasicWindow]\r\nSysTrayEnabled=true\r\nSysTrayWhenStarted=true\r\nPreviewEnabled=false\r\n");
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _installationGate.Dispose();

    private sealed record ObsRelease(
        [property: JsonPropertyName("tag_name")] string Version,
        [property: JsonPropertyName("assets")] IReadOnlyList<ObsReleaseAsset> Assets);

    private sealed record ObsReleaseAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] Uri DownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("size")] long Size);

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);
}
