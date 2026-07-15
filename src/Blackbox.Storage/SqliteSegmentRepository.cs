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
                file_size_bytes INTEGER NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
                is_hdr, is_protected, file_path, file_size_bytes)
            VALUES (
                $id, $session_id, $start_time, $end_time, $game_executable, $game_title,
                $video_format, $audio_track_layout, $encoder, $width, $height, $frame_rate,
                $is_hdr, $is_protected, $file_path, $file_size_bytes)
            ON CONFLICT(file_path) DO UPDATE SET
                end_time = excluded.end_time,
                file_size_bytes = excluded.file_size_bytes,
                is_protected = excluded.is_protected;
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

    public async Task MarkProtectedRangeAsync(DateTimeOffset startTime, DateTimeOffset endTime, CancellationToken cancellationToken = default)
    {
        if (endTime <= startTime)
        {
            throw new ArgumentException("Protected range end must be after start.", nameof(endTime));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE segments
            SET is_protected = 1
            WHERE start_time < $end_time AND end_time > $start_time;
            """;
        command.Parameters.AddWithValue("$start_time", startTime.ToString("O"));
        command.Parameters.AddWithValue("$end_time", endTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        command.Parameters.AddWithValue("$frame_rate", segment.FrameRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$is_hdr", segment.IsHdr ? 1 : 0);
        command.Parameters.AddWithValue("$is_protected", segment.IsProtected ? 1 : 0);
        command.Parameters.AddWithValue("$file_path", segment.FilePath);
        command.Parameters.AddWithValue("$file_size_bytes", segment.FileSizeBytes);
    }

    private static RecordingSegment ReadSegment(SqliteDataReader reader)
    {
        return new RecordingSegment(
            Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            Guid.Parse(reader.GetString(reader.GetOrdinal("session_id"))),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("start_time")), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("end_time")), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(reader.GetOrdinal("game_executable")),
            reader.GetString(reader.GetOrdinal("game_title")),
            reader.GetString(reader.GetOrdinal("video_format")),
            reader.GetString(reader.GetOrdinal("audio_track_layout")),
            reader.GetString(reader.GetOrdinal("encoder")),
            reader.GetInt32(reader.GetOrdinal("width")),
            reader.GetInt32(reader.GetOrdinal("height")),
            decimal.Parse(reader.GetString(reader.GetOrdinal("frame_rate")), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt32(reader.GetOrdinal("is_hdr")) == 1,
            reader.GetInt32(reader.GetOrdinal("is_protected")) == 1,
            reader.GetString(reader.GetOrdinal("file_path")),
            reader.GetInt64(reader.GetOrdinal("file_size_bytes")));
    }
}
