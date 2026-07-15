using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class ObsPortableProvisionerTests
{
    [Fact]
    public async Task EnsureInstalledAsync_downloads_verifies_and_extracts_official_package()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var archive = CreateObsArchive();
            var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var handler = new ReleaseHandler(archive, digest);
            var progress = new RecordingProgress();
            var provisioner = CreateProvisioner(testRoot, handler);

            var installation = await provisioner.EnsureInstalledAsync(
                progress);

            Assert.True(File.Exists(installation.ExecutablePath));
            Assert.True(File.Exists(Path.Combine(testRoot, "portable_mode.txt")));
            Assert.Contains(
                "FirstRun=true",
                await File.ReadAllTextAsync(Path.Combine(testRoot, "config", "obs-studio", "user.ini")));
            Assert.Equal("test-version", installation.Version);
            Assert.Contains(progress.Updates, static update => update.Stage == ObsSetupStage.Verifying);
            Assert.Contains(progress.Updates, static update => update.Stage == ObsSetupStage.Extracting);
        }
        finally
        {
            Directory.Delete(Directory.GetParent(testRoot)!.FullName, true);
        }
    }

    [Fact]
    public async Task EnsureInstalledAsync_reuses_existing_portable_copy_without_network()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var executable = Path.Combine(testRoot, "bin", "64bit", "obs64.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllTextAsync(executable, "test");
            var handler = new ReleaseHandler([], string.Empty);
            var provisioner = CreateProvisioner(testRoot, handler);

            var installation = await provisioner.EnsureInstalledAsync();

            Assert.Equal(executable, installation.ExecutablePath);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            Directory.Delete(Directory.GetParent(testRoot)!.FullName, true);
        }
    }

    [Fact]
    public async Task EnsureInstalledAsync_clones_discovered_installation_without_network()
    {
        var testRoot = CreateTestRoot();
        var parentDirectory = Directory.GetParent(testRoot)!.FullName;
        try
        {
            var existingRoot = Path.Combine(parentDirectory, "steam-obs");
            var executable = Path.Combine(existingRoot, "bin", "64bit", "obs64.exe");
            var dataFile = Path.Combine(existingRoot, "data", "obs-studio", "locale.ini");
            var existingConfiguration = Path.Combine(existingRoot, "config", "obs-studio", "user.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            Directory.CreateDirectory(Path.GetDirectoryName(dataFile)!);
            Directory.CreateDirectory(Path.Combine(existingRoot, "obs-plugins"));
            Directory.CreateDirectory(Path.GetDirectoryName(existingConfiguration)!);
            await File.WriteAllTextAsync(executable, "test executable");
            await File.WriteAllTextAsync(dataFile, "test data");
            await File.WriteAllTextAsync(existingConfiguration, "existing user settings");
            var handler = new ReleaseHandler([], string.Empty);
            var progress = new RecordingProgress();
            var provisioner = CreateProvisioner(
                testRoot,
                handler,
                new StubInstallationLocator(new ObsInstallation(existingRoot, executable, "steam-test")));

            var installation = await provisioner.EnsureInstalledAsync(progress);

            Assert.Equal("steam-test", installation.Version);
            Assert.Equal(0, handler.RequestCount);
            Assert.True(File.Exists(installation.ExecutablePath));
            Assert.Equal("test data", await File.ReadAllTextAsync(Path.Combine(testRoot, "data", "obs-studio", "locale.ini")));
            Assert.DoesNotContain(
                "existing user settings",
                await File.ReadAllTextAsync(Path.Combine(testRoot, "config", "obs-studio", "user.ini")));
            Assert.Contains(
                progress.Updates,
                static update => update.Stage == ObsSetupStage.CopyingExistingInstallation);
        }
        finally
        {
            Directory.Delete(parentDirectory, true);
        }
    }

    private static ObsPortableProvisioner CreateProvisioner(
        string testRoot,
        HttpMessageHandler handler,
        IObsInstallationLocator? installationLocator = null) =>
        new(
            new HttpClient(handler),
            new ObsProvisioningOptions
            {
                PortableRootDirectory = testRoot,
                LatestReleaseApiUri = new Uri("https://api.example.test/latest")
            },
            installationLocator ?? new StubInstallationLocator(null),
            NullLogger<ObsPortableProvisioner>.Instance);

    private static string CreateTestRoot() =>
        Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"), "obs-portable");

    private static byte[] CreateObsArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("OBS-Studio/bin/64bit/obs64.exe");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("test executable");
        }

        return stream.ToArray();
    }

    private sealed class ReleaseHandler(byte[] archive, string digest) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri?.Host == "api.example.test")
            {
                var json = JsonSerializer.Serialize(new
                {
                    tag_name = "test-version",
                    assets = new[]
                    {
                        new
                        {
                            name = "OBS-Studio-test-Windows-x64.zip",
                            browser_download_url = "https://downloads.example.test/obs.zip",
                            digest = $"sha256:{digest}",
                            size = archive.LongLength
                        }
                    }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            });
        }
    }

    private sealed class RecordingProgress : IProgress<ObsSetupProgress>
    {
        public List<ObsSetupProgress> Updates { get; } = [];

        public void Report(ObsSetupProgress value) => Updates.Add(value);
    }

    private sealed class StubInstallationLocator(ObsInstallation? installation) : IObsInstallationLocator
    {
        public ObsInstallation? FindExistingInstallation() => installation;
    }
}
