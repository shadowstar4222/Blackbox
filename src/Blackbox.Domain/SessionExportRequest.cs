namespace Blackbox.Domain;

public sealed record SessionExportRequest(
    RecordingSession Session,
    TimeSpan RangeStart,
    TimeSpan RangeEnd,
    string DestinationPath)
{
    public void Validate()
    {
        if (Session.Segments.Count == 0)
        {
            throw new InvalidOperationException("The recording session has no segments.");
        }

        if (RangeStart < TimeSpan.Zero || RangeEnd <= RangeStart || RangeEnd > Session.Duration)
        {
            throw new InvalidOperationException("The export selection is outside the recording session.");
        }

        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            throw new InvalidOperationException("An export destination is required.");
        }

        var extension = Path.GetExtension(DestinationPath);
        if (!extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Blackbox exports must use MKV or MP4.");
        }
    }
}
