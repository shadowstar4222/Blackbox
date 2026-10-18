using System.Globalization;
using Blackbox.Domain;
using Blackbox.Export;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualBasic.FileIO;

namespace Blackbox.Tests;

public sealed class NativeTimestampExportIntegrationTests
{
    [Fact]
    public async Task Timestamp_preserving_native_segments_join_without_video_reencoding()
    {
        var directory = Environment.GetEnvironmentVariable("BLACKBOX_SYNC_INTEGRATION_DIRECTORY");
        var tools = Environment.GetEnvironmentVariable("BLACKBOX_FFMPEG_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(tools)) return;
        var provisioner = new FixedProvisioner(tools);
        var probe = new FfprobeMediaProbe(provisioner, NullLogger<FfprobeMediaProbe>.Instance);
        var csv = Directory.GetFiles(directory, "*.segments.csv").Order(StringComparer.Ordinal).First();
        var segments = new List<RecordingSegment>();
        var sessionId = Guid.NewGuid();
        var origin = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        using (var parser = new TextFieldParser(csv))
        {
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields()!;
                var path = Path.Combine(directory, Path.GetFileName(fields[0]).Replace(".active.mkv", ".mkv", StringComparison.Ordinal));
                var media = await probe.ProbeAsync(path);
                var start = double.Parse(fields[1], CultureInfo.InvariantCulture);
                var end = double.Parse(fields[2], CultureInfo.InvariantCulture);
                var duration = TimeSpan.FromSeconds(Math.Min(end - start, media.Duration.TotalSeconds));
                segments.Add(TestSegments.Create(path, origin.AddSeconds(start), duration) with
                {
                    SessionId = sessionId, Encoder = media.VideoCodec, VideoFormat = media.PixelFormat,
                    Width = media.Width, Height = media.Height, FrameRate = media.FrameRate,
                    AudioTrackLayout = "1:full_mix", FileSizeBytes = new FileInfo(path).Length
                });
            }
        }
        Assert.True(segments.Count >= 2);
        Assert.All(segments, segment => Assert.Equal(segments[0].FrameRate, segment.FrameRate));
        var session = new RecordingSession(sessionId, segments[0].StartTime, segments[^1].EndTime,
            "Sync integration", segments, false, false);
        var options = new FfmpegOptions { RootDirectory = tools, WorkDirectory = Path.Combine(directory, "export-work") };
        var service = new SessionExportService(provisioner, new FfmpegCommandRunner(NullLogger<FfmpegCommandRunner>.Instance),
            new SegmentUsageRegistry(), options, NullLogger<SessionExportService>.Instance);
        var output = Path.Combine(directory, "export");
        foreach (var extension in new[] { ".mkv", ".mp4" })
        {
            var destination = Path.Combine(output, "recording" + extension);
            var result = await service.ExportAsync(new SessionExportRequest(session, TimeSpan.Zero, session.Duration, destination));
            Assert.True(result.UsedStreamCopy);
            var exported = await probe.ProbeAsync(destination);
            Assert.Equal(segments[0].Width, exported.Width);
            // The final AAC/microphone drain may outlast the last video frame. This
            // bounded tail is distinct from cumulative drift across segment joins.
            Assert.InRange(exported.Duration.TotalSeconds, session.Duration.TotalSeconds - .1, session.Duration.TotalSeconds + .75);
        }
    }

    private sealed class FixedProvisioner(string root) : IFfmpegProvisioner
    {
        public Task<FfmpegInstallation> EnsureInstalledAsync(IProgress<FfmpegProvisionProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new FfmpegInstallation(root,
                Path.Combine(root, "bin", "ffmpeg.exe"), Path.Combine(root, "bin", "ffprobe.exe")));
    }
}
