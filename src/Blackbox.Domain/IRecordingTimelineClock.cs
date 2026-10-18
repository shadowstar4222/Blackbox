namespace Blackbox.Domain;

public interface IRecordingTimelineClock
{
    // Timeline time excludes intervals where native window capture was paused.
    DateTimeOffset? RecordingTime { get; }
}
