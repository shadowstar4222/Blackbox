using Blackbox.Domain;

namespace Blackbox.Export;

public sealed class RecordingNoteService(IClipRepository repository, IClock clock)
{
    public async Task<IReadOnlyList<RecordingNote>> GetAsync(
        RecordingNoteTarget target,
        Guid targetId,
        CancellationToken cancellationToken = default) =>
        (await repository.GetNotesAsync(cancellationToken))
        .Where(note => note.Target == target && note.TargetId == targetId)
        .OrderByDescending(static note => note.UpdatedAt)
        .ToArray();

    public async Task<RecordingNote> SaveAsync(
        RecordingNoteTarget target,
        Guid targetId,
        string text,
        Guid? noteId = null,
        TimeSpan? rangeStart = null,
        TimeSpan? rangeEnd = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var existing = noteId is null
            ? null
            : (await repository.GetNotesAsync(cancellationToken))
                .FirstOrDefault(note => note.Id == noteId.Value);
        var note = new RecordingNote(
            noteId ?? Guid.NewGuid(),
            target,
            targetId,
            text.Trim(),
            existing?.CreatedAt ?? now,
            now,
            rangeStart,
            rangeEnd);
        note.Validate();
        await repository.UpsertNoteAsync(note, cancellationToken);
        return note;
    }

    public Task DeleteAsync(Guid noteId, CancellationToken cancellationToken = default) =>
        repository.DeleteNoteAsync(noteId, cancellationToken);
}
