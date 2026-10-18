using Blackbox.Domain;
using Microsoft.Data.Sqlite;

namespace Blackbox.Storage;

public sealed class SqliteClipRepository(string databasePath) : IClipRepository
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        DefaultTimeout = 5,
        Pooling = true
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS instant_clips (
                id TEXT PRIMARY KEY,
                requested_at TEXT NOT NULL,
                requested_start_time TEXT NOT NULL,
                requested_end_time TEXT NOT NULL,
                label TEXT NOT NULL,
                status INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                session_id TEXT NULL,
                actual_start_time TEXT NULL,
                actual_end_time TEXT NULL,
                game_title TEXT NULL,
                output_path TEXT NULL,
                failure_detail TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_instant_clips_status
                ON instant_clips(status, requested_at);
            CREATE TABLE IF NOT EXISTS clip_source_segments (
                clip_id TEXT NOT NULL,
                segment_id TEXT NOT NULL,
                PRIMARY KEY (clip_id, segment_id)
            );
            CREATE INDEX IF NOT EXISTS ix_clip_source_segments_segment
                ON clip_source_segments(segment_id);
            CREATE TABLE IF NOT EXISTS marker_source_segments (
                marker_id TEXT NOT NULL,
                segment_id TEXT NOT NULL,
                PRIMARY KEY (marker_id, segment_id)
            );
            CREATE INDEX IF NOT EXISTS ix_marker_source_segments_segment
                ON marker_source_segments(segment_id);
            CREATE TABLE IF NOT EXISTS recording_notes (
                id TEXT PRIMARY KEY,
                target INTEGER NOT NULL,
                target_id TEXT NOT NULL,
                text TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                range_start_ticks INTEGER NULL,
                range_end_ticks INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS ix_recording_notes_target
                ON recording_notes(target, target_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InstantClip>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var clips = new List<InstantClip>();
        var clipCommand = connection.CreateCommand();
        clipCommand.CommandText = "SELECT * FROM instant_clips ORDER BY requested_at DESC;";
        await using (var reader = await clipCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                clips.Add(ReadClip(reader));
            }
        }

        var sourceIds = new Dictionary<Guid, List<Guid>>();
        var sourceCommand = connection.CreateCommand();
        sourceCommand.CommandText = "SELECT clip_id, segment_id FROM clip_source_segments;";
        await using (var reader = await sourceCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var clipId = Guid.Parse(reader.GetString(0));
                if (!sourceIds.TryGetValue(clipId, out var ids))
                {
                    ids = [];
                    sourceIds[clipId] = ids;
                }

                ids.Add(Guid.Parse(reader.GetString(1)));
            }
        }

        return clips
            .Select(clip => clip with
            {
                SourceSegmentIds = sourceIds.TryGetValue(clip.Id, out var ids)
                    ? ids
                    : []
            })
            .ToArray();
    }

    public async Task<InstantClip?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        (await GetAllAsync(cancellationToken)).FirstOrDefault(clip => clip.Id == id);

    public async Task AddAsync(
        InstantClip clip,
        CancellationToken cancellationToken = default)
    {
        clip.Validate();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO instant_clips (
                id, requested_at, requested_start_time, requested_end_time,
                label, status, updated_at, session_id, actual_start_time,
                actual_end_time, game_title, output_path, failure_detail)
            VALUES (
                $id, $requested_at, $requested_start, $requested_end,
                $label, $status, $updated_at, $session_id, $actual_start,
                $actual_end, $game_title, $output_path, $failure_detail);
            """;
        AddClipParameters(command, clip);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task BeginFinalizationAsync(
        Guid clipId,
        Guid sessionId,
        DateTimeOffset actualStartTime,
        DateTimeOffset actualEndTime,
        IReadOnlyCollection<Guid> sourceSegmentIds,
        CancellationToken cancellationToken = default)
    {
        if (clipId == Guid.Empty ||
            sessionId == Guid.Empty ||
            actualEndTime <= actualStartTime ||
            sourceSegmentIds.Count == 0 ||
            sourceSegmentIds.Any(static id => id == Guid.Empty))
        {
            throw new ArgumentException("Clip finalization requires a valid session, range, and source segments.");
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE instant_clips
            SET session_id = $session_id,
                actual_start_time = $actual_start,
                actual_end_time = $actual_end,
                status = $status,
                failure_detail = NULL
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$id", clipId.ToString());
        update.Parameters.AddWithValue("$session_id", sessionId.ToString());
        update.Parameters.AddWithValue("$actual_start", actualStartTime.ToString("O"));
        update.Parameters.AddWithValue("$actual_end", actualEndTime.ToString("O"));
        update.Parameters.AddWithValue("$status", (int)InstantClipStatus.Pending);
        await update.ExecuteNonQueryAsync(cancellationToken);

        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM clip_source_segments WHERE clip_id = $clip_id;";
        delete.Parameters.AddWithValue("$clip_id", clipId.ToString());
        await delete.ExecuteNonQueryAsync(cancellationToken);

        foreach (var segmentId in sourceSegmentIds.Distinct())
        {
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO clip_source_segments (clip_id, segment_id)
                VALUES ($clip_id, $segment_id);
                """;
            insert.Parameters.AddWithValue("$clip_id", clipId.ToString());
            insert.Parameters.AddWithValue("$segment_id", segmentId.ToString());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task MarkReadyAsync(
        Guid clipId,
        string gameTitle,
        string outputPath,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameTitle) || string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("A ready clip requires a game title and output path.");
        }

        return UpdateClipAsync(
            clipId,
            """
            UPDATE instant_clips
            SET status = $status,
                game_title = $game_title,
                output_path = $output_path,
                failure_detail = NULL,
                updated_at = $updated_at
            WHERE id = $id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$status", (int)InstantClipStatus.Ready);
                command.Parameters.AddWithValue("$game_title", gameTitle);
                command.Parameters.AddWithValue("$output_path", Path.GetFullPath(outputPath));
                command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
            },
            cancellationToken);
    }

    public Task MarkFailedAsync(
        Guid clipId,
        string failureDetail,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(failureDetail))
        {
            throw new ArgumentException("A failed clip requires a failure detail.", nameof(failureDetail));
        }

        return UpdateClipAsync(
            clipId,
            """
            UPDATE instant_clips
            SET status = $status,
                failure_detail = $failure_detail,
                updated_at = $updated_at
            WHERE id = $id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$status", (int)InstantClipStatus.Failed);
                command.Parameters.AddWithValue("$failure_detail", failureDetail.Trim());
                command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
            },
            cancellationToken);
    }

    public Task ResetPendingAsync(
        Guid clipId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        UpdateClipAsync(
            clipId,
            """
            UPDATE instant_clips
            SET status = $status,
                failure_detail = NULL,
                updated_at = $updated_at
            WHERE id = $id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$status", (int)InstantClipStatus.Pending);
                command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
            },
            cancellationToken);

    public Task SetStatusAsync(
        Guid clipId,
        InstantClipStatus status,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (status is not (InstantClipStatus.Ready or
            InstantClipStatus.Protected or
            InstantClipStatus.Exported))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return UpdateClipAsync(
            clipId,
            """
            UPDATE instant_clips
            SET status = $status, updated_at = $updated_at
            WHERE id = $id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$status", (int)status);
                command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
            },
            cancellationToken);
    }

    public async Task DeleteAsync(Guid clipId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var statement in new[]
                 {
                     "DELETE FROM clip_source_segments WHERE clip_id = $id;",
                     "DELETE FROM recording_notes WHERE target = $target AND target_id = $id;",
                     "DELETE FROM instant_clips WHERE id = $id;"
                 })
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            command.Parameters.AddWithValue("$id", clipId.ToString());
            command.Parameters.AddWithValue("$target", (int)RecordingNoteTarget.InstantClip);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> GetRetainedSegmentIdsAsync(
        CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<Guid>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT source.segment_id
            FROM clip_source_segments source
            INNER JOIN instant_clips clip ON clip.id = source.clip_id
            WHERE clip.status IN ($pending, $failed)
            UNION
            SELECT DISTINCT segment_id
            FROM marker_source_segments;
            """;
        command.Parameters.AddWithValue("$pending", (int)InstantClipStatus.Pending);
        command.Parameters.AddWithValue("$failed", (int)InstantClipStatus.Failed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids;
    }

    public async Task SetMarkerSourceSegmentsAsync(
        Guid markerId,
        IReadOnlyCollection<Guid> sourceSegmentIds,
        CancellationToken cancellationToken = default)
    {
        if (markerId == Guid.Empty || sourceSegmentIds.Any(static id => id == Guid.Empty))
        {
            throw new ArgumentException("Marker source ownership requires valid IDs.");
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM marker_source_segments WHERE marker_id = $marker_id;";
        delete.Parameters.AddWithValue("$marker_id", markerId.ToString());
        await delete.ExecuteNonQueryAsync(cancellationToken);
        foreach (var segmentId in sourceSegmentIds.Distinct())
        {
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO marker_source_segments (marker_id, segment_id)
                VALUES ($marker_id, $segment_id);
                """;
            insert.Parameters.AddWithValue("$marker_id", markerId.ToString());
            insert.Parameters.AddWithValue("$segment_id", segmentId.ToString());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteMarkerDependenciesAsync(
        Guid markerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var statement in new[]
                 {
                     "DELETE FROM marker_source_segments WHERE marker_id = $id;",
                     "DELETE FROM recording_notes WHERE target = $target AND target_id = $id;"
                 })
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            command.Parameters.AddWithValue("$id", markerId.ToString());
            command.Parameters.AddWithValue("$target", (int)RecordingNoteTarget.Marker);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecordingNote>> GetNotesAsync(
        CancellationToken cancellationToken = default)
    {
        var notes = new List<RecordingNote>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM recording_notes ORDER BY updated_at DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var startOrdinal = reader.GetOrdinal("range_start_ticks");
            var endOrdinal = reader.GetOrdinal("range_end_ticks");
            notes.Add(new RecordingNote(
                Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                (RecordingNoteTarget)reader.GetInt32(reader.GetOrdinal("target")),
                Guid.Parse(reader.GetString(reader.GetOrdinal("target_id"))),
                reader.GetString(reader.GetOrdinal("text")),
                ParseTimestamp(reader.GetString(reader.GetOrdinal("created_at"))),
                ParseTimestamp(reader.GetString(reader.GetOrdinal("updated_at"))),
                reader.IsDBNull(startOrdinal)
                    ? null
                    : TimeSpan.FromTicks(reader.GetInt64(startOrdinal)),
                reader.IsDBNull(endOrdinal)
                    ? null
                    : TimeSpan.FromTicks(reader.GetInt64(endOrdinal))));
        }

        return notes;
    }

    public async Task UpsertNoteAsync(
        RecordingNote note,
        CancellationToken cancellationToken = default)
    {
        note.Validate();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recording_notes (
                id, target, target_id, text, created_at, updated_at,
                range_start_ticks, range_end_ticks)
            VALUES (
                $id, $target, $target_id, $text, $created_at, $updated_at,
                $range_start, $range_end)
            ON CONFLICT(id) DO UPDATE SET
                text = excluded.text,
                updated_at = excluded.updated_at,
                range_start_ticks = excluded.range_start_ticks,
                range_end_ticks = excluded.range_end_ticks;
            """;
        command.Parameters.AddWithValue("$id", note.Id.ToString());
        command.Parameters.AddWithValue("$target", (int)note.Target);
        command.Parameters.AddWithValue("$target_id", note.TargetId.ToString());
        command.Parameters.AddWithValue("$text", note.Text.Trim());
        command.Parameters.AddWithValue("$created_at", note.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", note.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$range_start",
            note.RangeStart is null ? DBNull.Value : note.RangeStart.Value.Ticks);
        command.Parameters.AddWithValue(
            "$range_end",
            note.RangeEnd is null ? DBNull.Value : note.RangeEnd.Value.Ticks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteNoteAsync(Guid noteId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recording_notes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", noteId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateClipAsync(
        Guid clipId,
        string commandText,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        if (clipId == Guid.Empty)
        {
            throw new ArgumentException("A clip ID is required.", nameof(clipId));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$id", clipId.ToString());
        configure(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static InstantClip ReadClip(SqliteDataReader reader)
    {
        var sessionOrdinal = reader.GetOrdinal("session_id");
        var actualStartOrdinal = reader.GetOrdinal("actual_start_time");
        var actualEndOrdinal = reader.GetOrdinal("actual_end_time");
        var gameTitleOrdinal = reader.GetOrdinal("game_title");
        var outputPathOrdinal = reader.GetOrdinal("output_path");
        var failureOrdinal = reader.GetOrdinal("failure_detail");
        return new InstantClip
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            RequestedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("requested_at"))),
            RequestedStartTime =
                ParseTimestamp(reader.GetString(reader.GetOrdinal("requested_start_time"))),
            RequestedEndTime =
                ParseTimestamp(reader.GetString(reader.GetOrdinal("requested_end_time"))),
            Label = reader.GetString(reader.GetOrdinal("label")),
            Status = (InstantClipStatus)reader.GetInt32(reader.GetOrdinal("status")),
            UpdatedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("updated_at"))),
            SessionId = reader.IsDBNull(sessionOrdinal)
                ? null
                : Guid.Parse(reader.GetString(sessionOrdinal)),
            ActualStartTime = reader.IsDBNull(actualStartOrdinal)
                ? null
                : ParseTimestamp(reader.GetString(actualStartOrdinal)),
            ActualEndTime = reader.IsDBNull(actualEndOrdinal)
                ? null
                : ParseTimestamp(reader.GetString(actualEndOrdinal)),
            GameTitle = reader.IsDBNull(gameTitleOrdinal)
                ? null
                : reader.GetString(gameTitleOrdinal),
            OutputPath = reader.IsDBNull(outputPathOrdinal)
                ? null
                : reader.GetString(outputPathOrdinal),
            FailureDetail = reader.IsDBNull(failureOrdinal)
                ? null
                : reader.GetString(failureOrdinal)
        };
    }

    private static void AddClipParameters(SqliteCommand command, InstantClip clip)
    {
        command.Parameters.AddWithValue("$id", clip.Id.ToString());
        command.Parameters.AddWithValue("$requested_at", clip.RequestedAt.ToString("O"));
        command.Parameters.AddWithValue("$requested_start", clip.RequestedStartTime.ToString("O"));
        command.Parameters.AddWithValue("$requested_end", clip.RequestedEndTime.ToString("O"));
        command.Parameters.AddWithValue("$label", clip.Label.Trim());
        command.Parameters.AddWithValue("$status", (int)clip.Status);
        command.Parameters.AddWithValue("$updated_at", clip.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$session_id",
            clip.SessionId is null ? DBNull.Value : clip.SessionId.Value.ToString());
        command.Parameters.AddWithValue(
            "$actual_start",
            clip.ActualStartTime is null ? DBNull.Value : clip.ActualStartTime.Value.ToString("O"));
        command.Parameters.AddWithValue(
            "$actual_end",
            clip.ActualEndTime is null ? DBNull.Value : clip.ActualEndTime.Value.ToString("O"));
        command.Parameters.AddWithValue("$game_title", clip.GameTitle ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$output_path", clip.OutputPath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$failure_detail", clip.FailureDetail ?? (object)DBNull.Value);
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
