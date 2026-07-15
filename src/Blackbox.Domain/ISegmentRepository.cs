namespace Blackbox.Domain;

public interface ISegmentRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(RecordingSegment segment, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecordingSegment>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<bool> ExistsByPathAsync(string filePath, CancellationToken cancellationToken = default);
}
