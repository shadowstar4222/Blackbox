using Blackbox.Domain;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Blackbox.Storage;

public sealed class SqliteGameProfileRepository(string databasePath) : IGameProfileRepository
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
            CREATE TABLE IF NOT EXISTS game_profiles (
                executable_path TEXT PRIMARY KEY COLLATE NOCASE,
                display_name TEXT NOT NULL,
                automatic_recording_enabled INTEGER NOT NULL,
                added_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                executable_aliases_json TEXT NOT NULL DEFAULT '[]',
                capture_game_audio INTEGER NOT NULL DEFAULT 1,
                follow_launcher_handoff INTEGER NOT NULL DEFAULT 1,
                prefer_gpu_activity INTEGER NOT NULL DEFAULT 0
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "executable_aliases_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "capture_game_audio", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(connection, "follow_launcher_handoff", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(connection, "prefer_gpu_activity", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
    }

    public async Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var profiles = new List<GameProfile>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM game_profiles ORDER BY display_name COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(new GameProfile(
                reader.GetString(reader.GetOrdinal("executable_path")),
                reader.GetString(reader.GetOrdinal("display_name")),
                reader.GetInt32(reader.GetOrdinal("automatic_recording_enabled")) == 1,
                DateTimeOffset.Parse(
                    reader.GetString(reader.GetOrdinal("added_at")),
                    System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(
                    reader.GetString(reader.GetOrdinal("updated_at")),
                    System.Globalization.CultureInfo.InvariantCulture))
            {
                ExecutableAliases = DeserializeAliases(reader.GetString(reader.GetOrdinal("executable_aliases_json"))),
                CaptureGameAudio = reader.GetInt32(reader.GetOrdinal("capture_game_audio")) == 1,
                FollowLauncherHandoff = reader.GetInt32(reader.GetOrdinal("follow_launcher_handoff")) == 1,
                PreferGpuActivity = reader.GetInt32(reader.GetOrdinal("prefer_gpu_activity")) == 1
            });
        }

        return profiles;
    }

    public async Task UpsertAsync(GameProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO game_profiles (
                executable_path, display_name, automatic_recording_enabled, added_at, updated_at,
                executable_aliases_json, capture_game_audio, follow_launcher_handoff, prefer_gpu_activity)
            VALUES ($path, $name, $enabled, $added_at, $updated_at,
                $aliases, $capture_audio, $follow_handoff, $prefer_gpu)
            ON CONFLICT(executable_path) DO UPDATE SET
                display_name = excluded.display_name,
                automatic_recording_enabled = excluded.automatic_recording_enabled,
                updated_at = excluded.updated_at,
                executable_aliases_json = excluded.executable_aliases_json,
                capture_game_audio = excluded.capture_game_audio,
                follow_launcher_handoff = excluded.follow_launcher_handoff,
                prefer_gpu_activity = excluded.prefer_gpu_activity;
            """;
        command.Parameters.AddWithValue("$path", Path.GetFullPath(profile.ExecutablePath));
        command.Parameters.AddWithValue("$name", profile.DisplayName);
        command.Parameters.AddWithValue("$enabled", profile.AutomaticRecordingEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$added_at", profile.AddedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", profile.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$aliases",
            JsonSerializer.Serialize(profile.ExecutableAliases.Select(Path.GetFullPath).ToArray()));
        command.Parameters.AddWithValue("$capture_audio", profile.CaptureGameAudio ? 1 : 0);
        command.Parameters.AddWithValue("$follow_handoff", profile.FollowLauncherHandoff ? 1 : 0);
        command.Parameters.AddWithValue("$prefer_gpu", profile.PreferGpuActivity ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM game_profiles WHERE executable_path = $path;";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(executablePath));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<string> DeserializeAliases(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        var inspectCommand = connection.CreateCommand();
        inspectCommand.CommandText = "PRAGMA table_info(game_profiles);";
        await using (var reader = await inspectCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(reader.GetOrdinal("name"))
                    .Equals(columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE game_profiles ADD COLUMN {columnName} {definition};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
