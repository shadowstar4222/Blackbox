using Blackbox.Domain;

namespace Blackbox.Tests;

internal sealed class InMemorySegmentRepository : ISegmentRepository
{
    private readonly List<RecordingSegment> _segments = [];
    private readonly List<TimelineMarker> _markers = [];
    private readonly List<ProtectedTimelineRange> _protectedRanges = [];

    public bool Initialized { get; private set; }
    public int UpsertCount { get; private set; }
    public long? Revision { get; private set; } = 0;
    public int MarkerReadCount { get; private set; }
    public int SegmentReadCount { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Initialized = true;
        return Task.CompletedTask;
    }

    public Task UpsertAsync(RecordingSegment segment, CancellationToken cancellationToken = default)
    {
        UpsertCount++;
        Revision++;
        _segments.RemoveAll(existing => existing.FilePath == segment.FilePath);
        _segments.Add(segment);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecordingSegment>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        SegmentReadCount++;
        return Task.FromResult<IReadOnlyList<RecordingSegment>>(_segments);
    }

    public Task<IReadOnlyList<TimelineMarker>> GetMarkersAsync(CancellationToken cancellationToken = default)
    {
        MarkerReadCount++;
        return Task.FromResult<IReadOnlyList<TimelineMarker>>(_markers);
    }

    public Task AddMarkerAsync(TimelineMarker marker, CancellationToken cancellationToken = default)
    {
        _markers.Add(marker);
        Revision++;
        return Task.CompletedTask;
    }

    public Task UpdateMarkerPlacementAsync(
        Guid markerId,
        Guid sessionId,
        TimeSpan offset,
        CancellationToken cancellationToken = default)
    {
        var index = _markers.FindIndex(marker => marker.Id == markerId);
        if (index >= 0)
        {
            _markers[index] = _markers[index] with
            {
                SessionId = sessionId,
                Offset = offset
            };
            Revision++;
        }

        return Task.CompletedTask;
    }

    public Task DeleteMarkerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _markers.RemoveAll(marker => marker.Id == id);
        Revision++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProtectedTimelineRange>> GetProtectedRangesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ProtectedTimelineRange>>(_protectedRanges);
    }

    public Task<bool> ExistsByPathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_segments.Any(segment => segment.FilePath == filePath));
    }

    public Task MarkProtectedRangeAsync(DateTimeOffset startTime, DateTimeOffset endTime, CancellationToken cancellationToken = default)
    {
        Revision++;
        _protectedRanges.Add(new ProtectedTimelineRange(
            Guid.NewGuid(),
            startTime,
            endTime,
            DateTimeOffset.UtcNow));
        for (var i = 0; i < _segments.Count; i++)
        {
            var segment = _segments[i];
            if (segment.StartTime < endTime && segment.EndTime > startTime)
            {
                _segments[i] = segment with { IsProtected = true };
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _segments.RemoveAll(segment => segment.Id == id);
        Revision++;
        return Task.CompletedTask;
    }

    public Task ReconcileMissingFilesAsync(CancellationToken cancellationToken = default)
    {
        if (_segments.RemoveAll(segment => !File.Exists(segment.FilePath)) > 0)
        {
            Revision++;
        }
        return Task.CompletedTask;
    }
}
