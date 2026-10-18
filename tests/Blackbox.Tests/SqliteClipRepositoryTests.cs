using Blackbox.Domain;
using Blackbox.Storage;

namespace Blackbox.Tests;

public sealed class SqliteClipRepositoryTests
{
    [Fact]
    public async Task Repository_persists_clip_lifecycle_and_source_ownership()
    {
        var database = CreateDatabasePath();
        var repository = new SqliteClipRepository(database);
        await repository.InitializeAsync();
        var clip = CreateClip();
        var sessionId = Guid.NewGuid();
        var sourceIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        await repository.AddAsync(clip);
        await repository.BeginFinalizationAsync(
            clip.Id,
            sessionId,
            clip.RequestedStartTime.AddSeconds(2),
            clip.RequestedEndTime,
            sourceIds);

        var pending = Assert.Single(await repository.GetAllAsync());
        Assert.Equal(sessionId, pending.SessionId);
        Assert.Equal(sourceIds.Order(), pending.SourceSegmentIds.Order());
        Assert.Equal(sourceIds.Order(), (await repository.GetRetainedSegmentIdsAsync()).Order());

        var output = Path.Combine(Path.GetTempPath(), "blackbox-tests", $"{Guid.NewGuid():N}.mkv");
        await repository.MarkReadyAsync(
            clip.Id,
            "Example Game",
            output,
            clip.UpdatedAt.AddSeconds(1));

        var restarted = new SqliteClipRepository(database);
        await restarted.InitializeAsync();
        var ready = Assert.Single(await restarted.GetAllAsync());
        Assert.Equal(InstantClipStatus.Ready, ready.Status);
        Assert.Equal("Example Game", ready.GameTitle);
        Assert.Equal(Path.GetFullPath(output), ready.OutputPath);
        Assert.Empty(await restarted.GetRetainedSegmentIdsAsync());
    }

    [Fact]
    public async Task Failed_clip_retains_sources_until_retry_or_discard()
    {
        var database = CreateDatabasePath();
        var repository = new SqliteClipRepository(database);
        await repository.InitializeAsync();
        var clip = CreateClip();
        var sourceId = Guid.NewGuid();
        await repository.AddAsync(clip);
        await repository.BeginFinalizationAsync(
            clip.Id,
            Guid.NewGuid(),
            clip.RequestedStartTime,
            clip.RequestedEndTime,
            [sourceId]);

        await repository.MarkFailedAsync(
            clip.Id,
            "Encoder failed",
            clip.UpdatedAt.AddSeconds(1));

        Assert.Contains(sourceId, await repository.GetRetainedSegmentIdsAsync());
        var failed = Assert.Single(await repository.GetAllAsync());
        Assert.Equal(InstantClipStatus.Failed, failed.Status);
        Assert.Equal("Encoder failed", failed.FailureDetail);

        await repository.ResetPendingAsync(clip.Id, clip.UpdatedAt.AddSeconds(2));
        Assert.Equal(
            InstantClipStatus.Pending,
            (await repository.GetByIdAsync(clip.Id))!.Status);
        await repository.DeleteAsync(clip.Id);
        Assert.Empty(await repository.GetAllAsync());
        Assert.Empty(await repository.GetRetainedSegmentIdsAsync());
    }

    [Fact]
    public async Task Repository_round_trips_all_note_targets_and_deletes_clip_notes_with_clip()
    {
        var database = CreateDatabasePath();
        var repository = new SqliteClipRepository(database);
        await repository.InitializeAsync();
        var clip = CreateClip();
        await repository.AddAsync(clip);
        var now = DateTimeOffset.Parse("2026-07-25T20:00:00Z");
        var notes = new[]
        {
            new RecordingNote(
                Guid.NewGuid(),
                RecordingNoteTarget.Session,
                Guid.NewGuid(),
                "Session note",
                now,
                now),
            new RecordingNote(
                Guid.NewGuid(),
                RecordingNoteTarget.Marker,
                Guid.NewGuid(),
                "Marker note",
                now,
                now),
            new RecordingNote(
                Guid.NewGuid(),
                RecordingNoteTarget.InstantClip,
                clip.Id,
                "Clip note",
                now,
                now),
            new RecordingNote(
                Guid.NewGuid(),
                RecordingNoteTarget.TimelineRange,
                Guid.NewGuid(),
                "Range note",
                now,
                now,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(25))
        };
        foreach (var note in notes)
        {
            await repository.UpsertNoteAsync(note);
        }

        Assert.Equal(4, (await repository.GetNotesAsync()).Count);
        var range = Assert.Single(
            (await repository.GetNotesAsync())
            .Where(static note => note.Target == RecordingNoteTarget.TimelineRange));
        Assert.Equal(TimeSpan.FromSeconds(10), range.RangeStart);
        Assert.Equal(TimeSpan.FromSeconds(25), range.RangeEnd);

        await repository.DeleteAsync(clip.Id);

        Assert.DoesNotContain(
            await repository.GetNotesAsync(),
            note => note.Target == RecordingNoteTarget.InstantClip && note.TargetId == clip.Id);
        Assert.Equal(3, (await repository.GetNotesAsync()).Count);
    }

    [Fact]
    public async Task Marker_dependencies_retain_sources_and_are_released_with_marker_notes()
    {
        var database = CreateDatabasePath();
        var repository = new SqliteClipRepository(database);
        await repository.InitializeAsync();
        var markerId = Guid.NewGuid();
        var sourceIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var now = DateTimeOffset.Parse("2026-07-25T20:00:00Z");
        await repository.SetMarkerSourceSegmentsAsync(markerId, sourceIds);
        await repository.UpsertNoteAsync(new RecordingNote(
            Guid.NewGuid(),
            RecordingNoteTarget.Marker,
            markerId,
            "Marker note",
            now,
            now));

        Assert.Equal(
            sourceIds.Order(),
            (await repository.GetRetainedSegmentIdsAsync()).Order());

        await repository.DeleteMarkerDependenciesAsync(markerId);

        Assert.Empty(await repository.GetRetainedSegmentIdsAsync());
        Assert.DoesNotContain(
            await repository.GetNotesAsync(),
            note => note.Target == RecordingNoteTarget.Marker && note.TargetId == markerId);
    }

    private static InstantClip CreateClip()
    {
        var now = DateTimeOffset.Parse("2026-07-25T20:00:00Z");
        return new InstantClip
        {
            Id = Guid.NewGuid(),
            RequestedAt = now,
            RequestedStartTime = now.AddMinutes(-2),
            RequestedEndTime = now,
            Label = "Last two minutes",
            Status = InstantClipStatus.Pending,
            UpdatedAt = now
        };
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "blackbox-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "blackbox.db");
    }
}
