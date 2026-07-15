namespace Blackbox.Domain;

public sealed record RecordingSettings
{
    public required string RecordingLocation { get; init; }
    public int SegmentDurationMinutes { get; init; } = 2;
    public string ContainerFormat { get; init; } = "mkv";
    public decimal MaximumStorageGigabytes { get; init; } = 50;
    public TimeSpan MaximumRetainedDuration { get; init; } = TimeSpan.FromHours(24);
    public decimal MinimumRemainingFreeDiskSpaceGigabytes { get; init; } = 10;

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

        if (MaximumStorageGigabytes <= 0)
        {
            throw new InvalidOperationException("Maximum storage must be greater than zero.");
        }

        if (MaximumRetainedDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Maximum retained duration must be greater than zero.");
        }

        if (MinimumRemainingFreeDiskSpaceGigabytes < 0)
        {
            throw new InvalidOperationException("Minimum free disk space cannot be negative.");
        }
    }
}
