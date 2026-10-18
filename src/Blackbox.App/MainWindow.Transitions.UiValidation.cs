using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

internal sealed record PlaybackTransitionConfiguration(string[] Sources, string Output, bool SaveVirtualInput = false,
    double[]? Durations = null);

public partial class MainWindow
{
    internal async Task ValidatePlaybackTransitionsAsync(string configuration)
    {
        var config = JsonSerializer.Deserialize<PlaybackTransitionConfiguration>(File.ReadAllText(configuration),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing transition configuration.");
        if (config.Sources.Length < 2 || config.Sources.Any(static path => !File.Exists(path)))
            throw new InvalidDataException("Transition validation needs at least two existing media files.");
        Show();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (_isRecoveryBusy) await Task.Delay(50, timeout.Token);
        await SelectNavigationAsync(RecordingsNavButton, OpenRecordingLibraryPageAsync);
        await _recordingLibraryPage!.ValidatePlaybackTransitionsAsync(config);
    }
}

public partial class RecordingLibraryPage
{
    internal async Task ValidatePlaybackTransitionsAsync(PlaybackTransitionConfiguration config)
    {
        var hashes = config.Sources.Select(HashSource).ToArray();
        var session = await BuildPlaybackFixtureSessionAsync(config.Sources, config.Durations);
        if (config.SaveVirtualInput)
        {
            Directory.CreateDirectory(config.Output);
            using var virtualInput = await Task.Run(() => Blackbox.Export.MatroskaPlaybackStream.Create(session.Segments));
            var path = Path.Combine(config.Output, $"virtual-playback-{Guid.NewGuid():N}.mkv");
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            await virtualInput.CopyToAsync(destination);
        }
        var windows = System.Windows.Application.Current.Windows.Count;
        await OpenPlaybackAsync(session, TimeSpan.Zero, allowMarkers: false);
        await _playbackPage!.ValidateTransitionsAsync(config.Output);
        await ClosePlaybackAsync();
        var canceledPage = new PlaybackPage(session, TimeSpan.Zero, _libraryService, _segmentUsageRegistry,
            _loggerFactory.CreateLogger<PlaybackPage>(), _playbackRuntime, await _playbackRuntime.GetAsync(),
            allowMarkers: false, startMuted: true);
        var preparation = canceledPage.PrepareContinuousInputAsync();
        await canceledPage.DisposeAsync();
        await preparation;
        for (var index = 0; index < config.Sources.Length; index++)
            if (HashSource(config.Sources[index]) != hashes[index])
                throw new InvalidOperationException("Transition validation changed a source file.");
        if (System.Windows.Application.Current.Windows.Count != windows)
            throw new InvalidOperationException("Transition validation leaked an overlay window.");
    }

    private async Task<RecordingSession> BuildPlaybackFixtureSessionAsync(IReadOnlyList<string> sources, double[]? durations = null)
    {
        if (durations is not null && (durations.Length != sources.Count || durations.Any(static value => !double.IsFinite(value) || value <= 0)))
            throw new InvalidDataException("Fixture durations must match the media files and be positive.");
        var origin = DateTimeOffset.UtcNow;
        var cursor = origin;
        var identity = Guid.NewGuid();
        var segments = new List<RecordingSegment>();
        foreach (var path in sources)
        {
            var media = await _mediaProbe.ProbeAsync(path);
            var duration = durations is null ? media.Duration : TimeSpan.FromSeconds(Math.Min(media.Duration.TotalSeconds, durations[segments.Count]));
            segments.Add(new RecordingSegment(Guid.NewGuid(), identity, cursor, cursor + duration,
                "fixture.exe", "Playback transition validation", media.PixelFormat, "1:full_mix", media.VideoCodec,
                media.Width, media.Height, media.FrameRate, media.IsHdr, false, path, new FileInfo(path).Length));
            cursor += duration;
        }
        return new RecordingSession(identity, origin, cursor, "Playback transition validation", segments, false, false);
    }

    private static string HashSource(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
