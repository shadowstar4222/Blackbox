using Blackbox.Domain;
using Microsoft.Data.Sqlite;

namespace Blackbox.Storage;

public sealed class SqliteSegmentRepository(string databasePath) : ISegmentRepository
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS segments (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                start_time TEXT NOT NULL,
                end_time TEXT NOT NULL,
                game_executable TEXT NOT NULL,
                game_title TEXT NOT NULL,
                video_format TEXT NOT NULL,
                audio_track_layout TEXT NOT NULL,
                encoder TEXT NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                frame_rate TEXT NOT NULL,
                is_hdr INTEGER NOT NULL,
                is_protected INTEGER NOT NULL,
                file_path TEXT NOT NULL UNIQUE,
                file_size_bytes INTEGER NOT NULL,
                is_damaged INTEGER NOT NULL DEFAULT 0,
                damage_detail TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS timeline_markers (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                offset_ticks INTEGER NOT NULL,
                label TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_timeline_markers_session
                ON timeline_markers(session_id, offset_ticks);
            CREATE TABLE IF NOT EXISTS protected_ranges (
                id TEXT PRIMARY KEY,
                start_time TEXT NOT NULL,
                end_time TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "is_damaged", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "damage_detail", "TEXT NULL", cancellationToken);
    }

    public async Task UpsertAsync(RecordingSegment segment, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO segments (
                id, session_id, start_time, end_time, game_executable, game_title,
                video_format, audio_track_layout, encoder, width, height, frame_rate,
                is_hdr, is_protected, file_path, file_size_bytes, is_damaged, damage_detail)
            VALUES (
                $id, $session_id, $start_time, $end_time, $game_executable, $game_title,
                $video_format, $audio_track_layout, $encoder, $width, $height, $frame_rate,
                $is_hdr, $is_protected, $file_path, $file_size_bytes, $is_damaged, $damage_detail)
            ON CONFLICT(file_path) DO UPDATE SET
                session_id = excluded.session_id,
                start_time = excluded.start_time,
                end_time = excluded.end_time,
                game_executable = excluded.game_executable,
                game_title = excluded.game_title,
                video_format = excluded.video_format,
                audio_track_layout = excluded.audio_track_layout,
                encoder = excluded.encoder,
                width = excluded.width,
                height = excluded.height,
                frame_rate = excluded.frame_rate,
                is_hdr = excluded.is_hdr,
                file_size_bytes = excluded.file_size_bytes,
                is_protected = MAX(segments.is_protected, excluded.is_protected),
                is_damaged = excluded.is_damaged,
                damage_detail = excluded.damage_detail;
            """;
        AddSegmentParameters(command, segment);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecordingSegment>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var segments = new List<RecordingSegment>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM segments ORDER BY start_time;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            segments.Add(ReadSegment(reader));
        }

        return segments;
    }

    public async Task<IReadOnlyList<TimelineMarker>> GetMarkersAsync(CancellationToken cancellationToken = default)
    {
        var markers = new List<TimelineMarker>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM timeline_markers ORDER BY session_id, offset_ticks;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            markers.Add(new TimelineMarker(
                Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                Guid.Parse(reader.GetString(reader.GetOrdinal("session_id"))),
                TimeSpan.FromTicks(reader.GetInt64(reader.GetOrdinal("offset_ticks"))),
                reader.GetString(reader.GetOrdinal("label")),
                DateTimeOffset.Parse(
                    reader.GetString(reader.GetOrdinal("created_at")),
                    System.Globalization.CultureInfo.InvariantCulture)));
        }

        return markers;
    }

    public async Task AddMarkerAsync(TimelineMarker marker, CancellationToken cancellationToken = default)
    {
        if (marker.Offset < TimeSpan.Zero || string.IsNullOrWhiteSpace(marker.Label))
        {
            throw new ArgumentException("Timeline markers require a non-negative offset and label.", nameof(marker));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO timeline_markers (id, session_id, offset_ticks, label, created_at)
            VALUES ($id, $session_id, $offset_ticks, $label, $created_at);
            """;
        command.Parameters.AddWithValue("$id", marker.Id.ToString());
        command.Parameters.AddWithValue("$session_id", marker.SessionId.ToString());
        command.Parameters.AddWithValue("$offset_ticks", marker.Offset.Ticks);
        command.Parameters.AddWithValue("$label", marker.Label);
        command.Parameters.AddWithValue("$created_at", marker.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteMarkerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM timeline_markers WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProtectedTimelineRange>> GetProtectedRangesAsync(
        CancellationToken cancellationToken = default)
    {
        var ranges = new List<ProtectedTimelineRange>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM protected_ranges ORDER BY start_time;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ranges.Add(new ProtectedTimelineRange(
                Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                DateTimeOffset.Parse(
                    reader.GetString(reader.GetOrdinal("start_time")),
                    System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(
                    reader.GetString(reader.GetOrdinal("end_time")),
                    System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(
                    reader.GetString(reader.GetOrdinal("created_at")),
                    System.Globalization.CultureInfo.InvariantCulture)));
        }

        return ranges;
    }

    public async Task<bool> ExistsByPathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM segments WHERE file_path = $file_path LIMIT 1;";
        command.Parameters.AddWithValue("$file_path", filePath);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null;
    }

    public async Task MarkProtectedRangeAsync(
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default)
    {
        if (endTime <= startTime)
        {
            throw new ArgumentException("Protected range end must be after start.", nameof(endTime));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = """
            UPDATE segments
            SET is_protected = 1
            WHERE start_time < $end_time AND end_time > $start_time;
            """;
        updateCommand.Parameters.AddWithValue("$start_time", startTime.ToString("O"));
        updateCommand.Parameters.AddWithValue("$end_time", endTime.ToString("O"));
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO protected_ranges (id, start_time, end_time, created_at)
            VALUES ($id, $start_time, $end_time, $created_at);
            """;
        insertCommand.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        insertCommand.Parameters.AddWithValue("$start_time", startTime.ToString("O"));
        insertCommand.Parameters.AddWithValue("$end_time", endTime.ToString("O"));
        insertCommand.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task DeleteByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM segments WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReconcileMissingFilesAsync(CancellationToken cancellationToken = default)
    {
        var segments = await GetAllAsync(cancellationToken);
        foreach (var segment in segments.Where(static segment => !File.Exists(segment.FilePath)))
        {
            await DeleteByIdAsync(segment.Id, cancellationToken);
        }
    }

    private static void AddSegmentParameters(SqliteCommand command, RecordingSegment segment)
    {
        command.Parameters.AddWithValue("$id", segment.Id.ToString());
        command.Parameters.AddWithValue("$session_id", segment.SessionId.ToString());
        command.Parameters.AddWithValue("$start_time", segment.StartTime.ToString("O"));
        command.Parameters.AddWithValue("$end_time", segment.EndTime.ToString("O"));
        command.Parameters.AddWithValue("$game_executable", segment.GameExecutable);
        command.Parameters.AddWithValue("$game_title", segment.GameTitle);
        command.Parameters.AddWithValue("$video_format", segment.VideoFormat);
        command.Parameters.AddWithValue("$audio_track_layout", segment.AudioTrackLayout);
        command.Parameters.AddWithValue("$encoder", segment.Encoder);
        command.Parameters.AddWithValue("$width", segment.Width);
        command.Parameters.AddWithValue("$height", segment.Height);
        command.Parameters.AddWithValue(
            "$frame_rate",
            segment.FrameRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$is_hdr", segment.IsHdr ? 1 : 0);
        command.Parameters.AddWithValue("$is_protected", segment.IsProtected ? 1 : 0);
        command.Parameters.AddWithValue("$file_path", segment.FilePath);
        command.Parameters.AddWithValue("$file_size_bytes", segment.FileSizeBytes);
        command.Parameters.AddWithValue("$is_damaged", segment.IsDamaged ? 1 : 0);
        command.Parameters.AddWithValue("$damage_detail", (object?)segment.DamageDetail ?? DBNull.Value);
    }

    private static RecordingSegment ReadSegment(SqliteDataReader reader)
    {
        return new RecordingSegment(
            Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            Guid.Parse(reader.GetString(reader.GetOrdinal("session_id"))),
            DateTimeOffset.Parse(
                reader.GetString(reader.GetOrdinal("start_time")),
                System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(
                reader.GetString(reader.GetOrdinal("end_time")),
                System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(reader.GetOrdinal("game_executable")),
            reader.GetString(reader.GetOrdinal("game_title")),
            reader.GetString(reader.GetOrdinal("video_format")),
            reader.GetString(reader.GetOrdinal("audio_track_layout")),
            reader.GetString(reader.GetOrdinal("encoder")),
            reader.GetInt32(reader.GetOrdinal("width")),
            reader.GetInt32(reader.GetOrdinal("height")),
            decimal.Parse(
                reader.GetString(reader.GetOrdinal("frame_rate")),
                System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt32(reader.GetOrdinal("is_hdr")) == 1,
            reader.GetInt32(reader.GetOrdinal("is_protected")) == 1,
            reader.GetString(reader.GetOrdinal("file_path")),
            reader.GetInt64(reader.GetOrdinal("file_size_bytes")),
            reader.GetInt32(reader.GetOrdinal("is_damaged")) == 1,
            reader.IsDBNull(reader.GetOrdinal("damage_detail"))
                ? null
                : reader.GetString(reader.GetOrdinal("damage_detail")));
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        var inspectCommand = connection.CreateCommand();
        inspectCommand.CommandText = "PRAGMA table_info(segments);";
        var exists = false;
        await using (var reader = await inspectCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(reader.GetOrdinal("name"))
                    .Equals(columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE segments ADD COLUMN {columnName} {definition};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
