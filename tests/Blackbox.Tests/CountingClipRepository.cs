using Blackbox.Domain;

namespace Blackbox.Tests;

internal sealed class CountingClipRepository(IClipRepository inner) : IClipRepository
{
    public int GetAllCount { get; private set; }
    public int MarkerOwnershipWrites { get; private set; }

    public Task<IReadOnlyList<InstantClip>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        GetAllCount++;
        return inner.GetAllAsync(cancellationToken);
    }

    public Task SetMarkerSourceSegmentsAsync(Guid markerId, IReadOnlyCollection<Guid> sourceSegmentIds,
        CancellationToken cancellationToken = default)
    {
        MarkerOwnershipWrites++;
        return inner.SetMarkerSourceSegmentsAsync(markerId, sourceSegmentIds, cancellationToken);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);
    public Task<InstantClip?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);
    public Task AddAsync(InstantClip clip, CancellationToken cancellationToken = default) => inner.AddAsync(clip, cancellationToken);
    public Task BeginFinalizationAsync(Guid clipId, Guid sessionId, DateTimeOffset actualStartTime,
        DateTimeOffset actualEndTime, IReadOnlyCollection<Guid> sourceSegmentIds, CancellationToken cancellationToken = default) =>
        inner.BeginFinalizationAsync(clipId, sessionId, actualStartTime, actualEndTime, sourceSegmentIds, cancellationToken);
    public Task MarkReadyAsync(Guid clipId, string gameTitle, string outputPath, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) => inner.MarkReadyAsync(clipId, gameTitle, outputPath, updatedAt, cancellationToken);
    public Task MarkFailedAsync(Guid clipId, string failureDetail, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) => inner.MarkFailedAsync(clipId, failureDetail, updatedAt, cancellationToken);
    public Task ResetPendingAsync(Guid clipId, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
        inner.ResetPendingAsync(clipId, updatedAt, cancellationToken);
    public Task SetStatusAsync(Guid clipId, InstantClipStatus status, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) => inner.SetStatusAsync(clipId, status, updatedAt, cancellationToken);
    public Task DeleteAsync(Guid clipId, CancellationToken cancellationToken = default) => inner.DeleteAsync(clipId, cancellationToken);
    public Task DeleteMarkerDependenciesAsync(Guid markerId, CancellationToken cancellationToken = default) =>
        inner.DeleteMarkerDependenciesAsync(markerId, cancellationToken);
    public Task<IReadOnlySet<Guid>> GetRetainedSegmentIdsAsync(CancellationToken cancellationToken = default) =>
        inner.GetRetainedSegmentIdsAsync(cancellationToken);
    public Task<IReadOnlyList<RecordingNote>> GetNotesAsync(CancellationToken cancellationToken = default) => inner.GetNotesAsync(cancellationToken);
    public Task UpsertNoteAsync(RecordingNote note, CancellationToken cancellationToken = default) => inner.UpsertNoteAsync(note, cancellationToken);
    public Task DeleteNoteAsync(Guid noteId, CancellationToken cancellationToken = default) => inner.DeleteNoteAsync(noteId, cancellationToken);
}
