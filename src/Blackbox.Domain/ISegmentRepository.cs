namespace Blackbox.Domain;

public interface ISegmentRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(RecordingSegment segment, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecordingSegment>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TimelineMarker>> GetMarkersAsync(CancellationToken cancellationToken = default);
    Task AddMarkerAsync(TimelineMarker marker, CancellationToken cancellationToken = default);
    Task DeleteMarkerAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProtectedTimelineRange>> GetProtectedRangesAsync(CancellationToken cancellationToken = default);
    Task<bool> ExistsByPathAsync(string filePath, CancellationToken cancellationToken = default);
    Task MarkProtectedRangeAsync(DateTimeOffset startTime, DateTimeOffset endTime, CancellationToken cancellationToken = default);
    Task DeleteByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task ReconcileMissingFilesAsync(CancellationToken cancellationToken = default);
}
