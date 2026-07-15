namespace Blackbox.Domain;

public sealed record RecordingSettings
{
    public required string RecordingLocation { get; init; }
    public int SegmentDurationMinutes { get; init; } = 2;
    public string ContainerFormat { get; init; } = "mkv";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RecordingLocation))
        {
            throw new InvalidOperationException("Recording location is required.");
        }

        if (SegmentDurationMinutes is < 1 or > 10)
        {
            throw new InvalidOperationException("Segment duration must be between 1 and 10 minutes.");
        }
    }
}
