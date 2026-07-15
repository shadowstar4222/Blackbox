using Blackbox.Domain;

namespace Blackbox.Export;

public sealed class RecordingSessionCatalog(ISegmentRepository repository)
{
    private static readonly TimeSpan GapTolerance = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyList<RecordingSession>> GetSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var segments = await repository.GetAllAsync(cancellationToken);
        var markers = await repository.GetMarkersAsync(cancellationToken);
        var protectedRanges = await repository.GetProtectedRangesAsync(cancellationToken);
        return Build(segments)
            .Select(session => session with
            {
                Markers = markers
                    .Where(marker => marker.SessionId == session.Id && marker.Offset <= session.Duration)
                    .OrderBy(static marker => marker.Offset)
                    .ToArray(),
                ProtectedRanges = GetProtectedRanges(session, protectedRanges)
            })
            .ToArray();
    }

    internal static IReadOnlyList<RecordingSession> Build(IReadOnlyList<RecordingSegment> segments)
    {
        return segments
            .GroupBy(static segment => segment.SessionId)
            .Select(CreateSession)
            .OrderByDescending(static session => session.StartTime)
            .ToArray();
    }

    private static RecordingSession CreateSession(IGrouping<Guid, RecordingSegment> group)
    {
        var segments = group.OrderBy(static segment => segment.StartTime).ToArray();
        var hasGaps = false;
        for (var index = 1; index < segments.Length; index++)
        {
            if (segments[index].StartTime - segments[index - 1].EndTime > GapTolerance)
            {
                hasGaps = true;
                break;
            }
        }

        var title = segments
            .Select(static segment => segment.GameTitle)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value) && value != "Recording")
            ?? "Recording";
        return new RecordingSession(
            group.Key,
            segments[0].StartTime,
            segments[^1].EndTime,
            title,
            segments,
            hasGaps,
            segments.Any(static segment => !File.Exists(segment.FilePath)));
    }

    private static IReadOnlyList<ProtectedTimelineRange> GetProtectedRanges(
        RecordingSession session,
        IReadOnlyList<ProtectedTimelineRange> persistedRanges)
    {
        var ranges = persistedRanges
            .Where(range => range.StartTime < session.EndTime && range.EndTime > session.StartTime)
            .ToList();
        foreach (var segment in session.Segments.Where(static segment => segment.IsProtected))
        {
            if (ranges.Any(range => range.StartTime < segment.EndTime && range.EndTime > segment.StartTime))
            {
                continue;
            }

            ranges.Add(new ProtectedTimelineRange(
                segment.Id,
                segment.StartTime,
                segment.EndTime,
                segment.StartTime));
        }

        return ranges.OrderBy(static range => range.StartTime).ToArray();
    }
}
