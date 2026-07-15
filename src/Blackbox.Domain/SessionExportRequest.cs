namespace Blackbox.Domain;

public sealed record SessionExportRequest(
    RecordingSession Session,
    TimeSpan RangeStart,
    TimeSpan RangeEnd,
    string DestinationPath,
    IReadOnlyList<AudioTrackExportSelection>? AudioTracks = null)
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

        if (AudioTracks is null)
        {
            return;
        }

        if (AudioTracks.Select(static track => track.StreamIndex).Distinct().Count() != AudioTracks.Count)
        {
            throw new InvalidOperationException("Each export audio stream can be configured only once.");
        }

        if (AudioTracks.Any(static track =>
            track.StreamIndex < 0 ||
            string.IsNullOrWhiteSpace(track.Name) ||
            track.Volume is < 0 or > 2))
        {
            throw new InvalidOperationException("Export audio settings contain an invalid stream, name, or volume.");
        }
    }
}
