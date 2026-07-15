using Blackbox.Domain;
using Blackbox.Export;

namespace Blackbox.Tests;

public sealed class SessionPlaybackServiceTests
{
    [Fact]
    public void BuildArguments_starts_the_continuous_player_at_the_scrub_cursor()
    {
        var segment = TestSegments.Create("C:\\Recordings\\one.mkv", duration: TimeSpan.FromMinutes(2));
        var session = new RecordingSession(
            segment.SessionId,
            segment.StartTime,
            segment.EndTime,
            "Recording",
            [segment],
            false,
            false);

        var arguments = SessionPlaybackService.BuildArguments(
            session,
            TimeSpan.FromSeconds(42.5),
            "timeline.ffconcat");

        var seekIndex = Array.IndexOf(arguments.ToArray(), "-ss");
        Assert.Equal(["-ss", "42.5", "-i", "timeline.ffconcat"], arguments.Skip(seekIndex).Take(4));
    }
}
