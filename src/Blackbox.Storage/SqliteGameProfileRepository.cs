using Blackbox.Domain;
using Microsoft.Data.Sqlite;

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
                updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
                    System.Globalization.CultureInfo.InvariantCulture)));
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
                executable_path, display_name, automatic_recording_enabled, added_at, updated_at)
            VALUES ($path, $name, $enabled, $added_at, $updated_at)
            ON CONFLICT(executable_path) DO UPDATE SET
                display_name = excluded.display_name,
                automatic_recording_enabled = excluded.automatic_recording_enabled,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$path", Path.GetFullPath(profile.ExecutablePath));
        command.Parameters.AddWithValue("$name", profile.DisplayName);
        command.Parameters.AddWithValue("$enabled", profile.AutomaticRecordingEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$added_at", profile.AddedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", profile.UpdatedAt.ToString("O"));
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
}
