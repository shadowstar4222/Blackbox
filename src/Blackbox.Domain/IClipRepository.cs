namespace Blackbox.Domain;

public interface IClipRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstantClip>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<InstantClip?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(InstantClip clip, CancellationToken cancellationToken = default);
    Task BeginFinalizationAsync(
        Guid clipId,
        Guid sessionId,
        DateTimeOffset actualStartTime,
        DateTimeOffset actualEndTime,
        IReadOnlyCollection<Guid> sourceSegmentIds,
        CancellationToken cancellationToken = default);
    Task MarkReadyAsync(
        Guid clipId,
        string gameTitle,
        string outputPath,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
    Task MarkFailedAsync(
        Guid clipId,
        string failureDetail,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
    Task ResetPendingAsync(
        Guid clipId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
    Task SetStatusAsync(
        Guid clipId,
        InstantClipStatus status,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid clipId, CancellationToken cancellationToken = default);
    Task SetMarkerSourceSegmentsAsync(
        Guid markerId,
        IReadOnlyCollection<Guid> sourceSegmentIds,
        CancellationToken cancellationToken = default);
    Task DeleteMarkerDependenciesAsync(
        Guid markerId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlySet<Guid>> GetRetainedSegmentIdsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecordingNote>> GetNotesAsync(CancellationToken cancellationToken = default);
    Task UpsertNoteAsync(RecordingNote note, CancellationToken cancellationToken = default);
    Task DeleteNoteAsync(Guid noteId, CancellationToken cancellationToken = default);
}
