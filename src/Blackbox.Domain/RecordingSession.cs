namespace Blackbox.Domain;

public sealed record RecordingSession(
    Guid Id,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    string GameTitle,
    IReadOnlyList<RecordingSegment> Segments,
    bool HasGaps,
    bool HasMissingSegments)
{
    public TimeSpan Duration => TimeSpan.FromTicks(
        Segments.Sum(static segment => (segment.EndTime - segment.StartTime).Ticks));
    public bool IsProtected => Segments.Any(static segment => segment.IsProtected);
}
