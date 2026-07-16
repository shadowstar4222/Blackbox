using Blackbox.Domain;
using Blackbox.Storage;
using Microsoft.Data.Sqlite;

namespace Blackbox.Tests;

public sealed class SqliteGameProfileRepositoryTests
{
    [Fact]
    public async Task Repository_persists_updates_and_removes_game_profiles()
    {
        var database = Path.Combine(Path.GetTempPath(), "blackbox-tests", $"{Guid.NewGuid():N}.db");
        var repository = new SqliteGameProfileRepository(database);
        await repository.InitializeAsync();
        var addedAt = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        var profile = new GameProfile(
            "C:\\Games\\Example\\Example.exe",
            "Example Game",
            true,
            addedAt,
            addedAt)
        {
            ExecutableAliases = ["C:\\Games\\Example\\Example-Win64.exe"],
            CaptureGameAudio = false,
            FollowLauncherHandoff = false,
            PreferGpuActivity = true
        };

        await repository.UpsertAsync(profile);
        await repository.UpsertAsync(profile with
        {
            AutomaticRecordingEnabled = false,
            UpdatedAt = addedAt.AddMinutes(1)
        });

        var stored = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("Example Game", stored.DisplayName);
        Assert.False(stored.AutomaticRecordingEnabled);
        Assert.Equal(profile.ExecutableAliases, stored.ExecutableAliases);
        Assert.False(stored.CaptureGameAudio);
        Assert.False(stored.FollowLauncherHandoff);
        Assert.True(stored.PreferGpuActivity);
        Assert.Equal(addedAt, stored.AddedAt);
        Assert.Equal(addedAt.AddMinutes(1), stored.UpdatedAt);

        await repository.DeleteAsync(profile.ExecutablePath);
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task InitializeAsync_migrates_legacy_profiles_with_safe_defaults()
    {
        var database = Path.Combine(Path.GetTempPath(), "blackbox-tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE game_profiles (
                    executable_path TEXT PRIMARY KEY COLLATE NOCASE,
                    display_name TEXT NOT NULL,
                    automatic_recording_enabled INTEGER NOT NULL,
                    added_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO game_profiles VALUES (
                    'C:\Games\Legacy\Legacy.exe', 'Legacy', 1,
                    '2026-07-16T12:00:00.0000000+00:00',
                    '2026-07-16T12:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteGameProfileRepository(database);
        await repository.InitializeAsync();

        var migrated = Assert.Single(await repository.GetAllAsync());
        Assert.Empty(migrated.ExecutableAliases);
        Assert.True(migrated.CaptureGameAudio);
        Assert.True(migrated.FollowLauncherHandoff);
        Assert.False(migrated.PreferGpuActivity);
    }
}
