using Blackbox.Domain;

namespace Blackbox.Tests;

public sealed class RecordingDirectoryLayoutTests
{
    [Fact]
    public void GetSessionDirectory_organizes_and_sanitizes_application_recordings()
    {
        var root = Path.Combine(Path.GetTempPath(), "Blackbox");
        var recordedAt = DateTimeOffset.Parse("2026-07-18T19:30:00-04:00");

        var path = RecordingDirectoryLayout.GetSessionDirectory(
            root,
            "Example: The Game?",
            recordedAt);

        Assert.Equal(
            Path.Combine(
                Path.GetFullPath(root),
                "Example- The Game-",
                recordedAt.ToLocalTime().ToString("yyyy-MM-dd")),
            path);
        Assert.Equal(
            "Example- The Game-",
            RecordingDirectoryLayout.GetApplicationName(
                root,
                Path.Combine(path, "2026-07-18 19-30-00.mkv")));
    }

    [Fact]
    public void GetSessionDirectory_replaces_reserved_windows_names()
    {
        var path = RecordingDirectoryLayout.GetSessionDirectory(
            "C:\\Recordings",
            "CON",
            DateTimeOffset.Parse("2026-07-18T12:00:00Z"));

        Assert.Contains($"{Path.DirectorySeparatorChar}Recording{Path.DirectorySeparatorChar}", path);
    }

    [Fact]
    public void IsInternalPath_excludes_generated_clips_from_recording_sessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "Blackbox");
        var clip = Path.Combine(
            root,
            RecordingDirectoryLayout.InstantClipName,
            "Example Game",
            "2026-07-25",
            "clip.mkv");

        Assert.True(RecordingDirectoryLayout.IsInternalPath(root, clip));
        Assert.False(RecordingDirectoryLayout.IsInternalPath(
            root,
            Path.Combine(root, "Example Game", "2026-07-25", "recording.mkv")));
    }
}
