using Blackbox.Domain;
using Blackbox.Storage;

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
            addedAt);

        await repository.UpsertAsync(profile);
        await repository.UpsertAsync(profile with
        {
            AutomaticRecordingEnabled = false,
            UpdatedAt = addedAt.AddMinutes(1)
        });

        var stored = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("Example Game", stored.DisplayName);
        Assert.False(stored.AutomaticRecordingEnabled);
        Assert.Equal(addedAt, stored.AddedAt);
        Assert.Equal(addedAt.AddMinutes(1), stored.UpdatedAt);

        await repository.DeleteAsync(profile.ExecutablePath);
        Assert.Empty(await repository.GetAllAsync());
    }
}
