namespace Blackbox.Domain;

public enum RecordingNoteTarget
{
    Session,
    Marker,
    InstantClip,
    TimelineRange
}

public sealed record RecordingNote(
    Guid Id,
    RecordingNoteTarget Target,
    Guid TargetId,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    TimeSpan? RangeStart = null,
    TimeSpan? RangeEnd = null)
{
    public void Validate()
    {
        if (Id == Guid.Empty ||
            TargetId == Guid.Empty ||
            !Enum.IsDefined(Target) ||
            string.IsNullOrWhiteSpace(Text) ||
            Text.Length > 4000)
        {
            throw new InvalidOperationException("The recording note is invalid.");
        }

        if ((RangeStart is null) != (RangeEnd is null) ||
            RangeStart < TimeSpan.Zero ||
            RangeEnd <= RangeStart)
        {
            throw new InvalidOperationException("The recording note range is invalid.");
        }

        if (Target == RecordingNoteTarget.TimelineRange && RangeStart is null)
        {
            throw new InvalidOperationException("A timeline-range note requires a range.");
        }
    }
}
