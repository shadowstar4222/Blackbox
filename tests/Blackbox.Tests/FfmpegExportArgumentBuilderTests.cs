using Blackbox.Domain;
using Blackbox.Export;

namespace Blackbox.Tests;

public sealed class FfmpegExportArgumentBuilderTests
{
    [Fact]
    public void Build_uses_stream_copy_for_a_full_session()
    {
        var session = CreateSession(TimeSpan.FromMinutes(4));
        var request = new SessionExportRequest(session, TimeSpan.Zero, session.Duration, "C:\\Exports\\session.mkv");

        var arguments = FfmpegExportArgumentBuilder.Build(request, "list.ffconcat", "partial.mkv", out var streamCopy);

        Assert.True(streamCopy);
        Assert.Contains("copy", arguments);
        Assert.DoesNotContain("libx264", arguments);
        Assert.Contains("title=Full listening mix", arguments);
    }

    [Fact]
    public void Build_reencodes_a_trimmed_range_for_accurate_boundaries()
    {
        var session = CreateSession(TimeSpan.FromMinutes(4));
        var request = new SessionExportRequest(
            session,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMinutes(3),
            "C:\\Exports\\clip.mp4");

        var arguments = FfmpegExportArgumentBuilder.Build(request, "list.ffconcat", "partial.mp4", out var streamCopy);

        Assert.False(streamCopy);
        Assert.Contains("libx264", arguments);
        Assert.Contains("15", arguments);
        Assert.Contains("165", arguments);
        Assert.Contains("+faststart", arguments);
    }

    [Fact]
    public void Build_copies_full_video_but_reencodes_only_selected_audio_changes()
    {
        var session = CreateSession(TimeSpan.FromMinutes(4));
        var tracks = RecordingAudioLayout.CreateExportSelections(session.Segments[0].AudioTrackLayout)
            .Select(track => track.StreamIndex switch
            {
                2 => track with { IsMuted = true },
                1 => track with { Volume = 0.5 },
                _ => track
            })
            .ToArray();
        var request = new SessionExportRequest(
            session,
            TimeSpan.Zero,
            session.Duration,
            "C:\\Exports\\session.mkv",
            tracks);

        var arguments = FfmpegExportArgumentBuilder.Build(request, "list.ffconcat", "partial.mkv", out var streamCopy);

        Assert.False(streamCopy);
        var videoCodecIndex = Array.IndexOf(arguments.ToArray(), "-c:v");
        var audioCodecIndex = Array.IndexOf(arguments.ToArray(), "-c:a");
        Assert.Equal(["-c:v", "copy"], arguments.Skip(videoCodecIndex).Take(2));
        Assert.Equal(["-c:a", "aac"], arguments.Skip(audioCodecIndex).Take(2));
        Assert.Contains("volume=0.5", arguments);
        Assert.DoesNotContain("0:a:2?", arguments);
    }

    [Fact]
    public void Audio_export_builds_a_trimmed_24_bit_wav_for_the_chosen_track()
    {
        var session = CreateSession(TimeSpan.FromMinutes(4));
        var track = new AudioTrackExportSelection(3, "Raw microphone", Volume: 1.25, ExportAsWav: true);
        var request = new SessionExportRequest(
            session,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            "C:\\Exports\\clip.mkv",
            [track]);

        var arguments = FfmpegAudioExportArgumentBuilder.Build(request, track, "list.ffconcat", "raw.wav");

        Assert.Contains("0:a:3", arguments);
        Assert.Contains("pcm_s24le", arguments);
        Assert.Contains("volume=1.25", arguments);
        Assert.Contains("10", arguments);
    }

    private static RecordingSession CreateSession(TimeSpan duration)
    {
        var start = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
        var segment = TestSegments.Create("C:\\Recordings\\one.mkv", start, duration);
        return new RecordingSession(segment.SessionId, start, start + duration, "Recording", [segment], false, false);
    }
}
