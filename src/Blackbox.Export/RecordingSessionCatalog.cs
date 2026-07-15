using Blackbox.Domain;

namespace Blackbox.Export;

public sealed class RecordingSessionCatalog(ISegmentRepository repository)
{
    private static readonly TimeSpan GapTolerance = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyList<RecordingSession>> GetSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var segments = await repository.GetAllAsync(cancellationToken);
        return Build(segments);
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
}
