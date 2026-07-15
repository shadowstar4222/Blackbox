using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Blackbox.Export;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class FfmpegProvisionerTests
{
    [Fact]
    public async Task EnsureInstalledAsync_verifies_and_extracts_the_windows_tools_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = CreatePackage();
            var checksum = Convert.ToHexString(SHA256.HashData(package));
            var handler = new PackageHandler(package, checksum);
            var options = new FfmpegOptions
            {
                RootDirectory = Path.Combine(root, "ffmpeg"),
                WorkDirectory = Path.Combine(root, "work"),
                PackageUri = new Uri("https://example.test/ffmpeg.zip"),
                ChecksumUri = new Uri("https://example.test/ffmpeg.zip.sha256")
            };
            var provisioner = new FfmpegProvisioner(
                new HttpClient(handler),
                options,
                NullLogger<FfmpegProvisioner>.Instance);

            var first = await provisioner.EnsureInstalledAsync();
            var second = await provisioner.EnsureInstalledAsync();

            Assert.True(File.Exists(first.FfmpegPath));
            Assert.True(File.Exists(first.FfprobePath));
            Assert.True(File.Exists(first.FfplayPath));
            Assert.Equal(first, second);
            Assert.Equal(2, handler.Requests);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static byte[] CreatePackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe", "ffplay.exe" })
            {
                var entry = archive.CreateEntry($"ffmpeg-test/bin/{name}");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(name);
            }
        }

        return stream.ToArray();
    }

    private sealed class PackageHandler(byte[] package, string checksum) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            var content = request.RequestUri?.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) == true
                ? new StringContent(checksum)
                : new ByteArrayContent(package);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
