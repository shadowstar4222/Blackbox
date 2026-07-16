using Blackbox.Domain;

namespace Blackbox.Tests;

public sealed class GameProfileTests
{
    [Fact]
    public void Validate_accepts_unique_full_path_aliases()
    {
        var profile = CreateProfile() with
        {
            ExecutableAliases = ["C:\\Games\\Example\\Game.exe"]
        };

        profile.Validate();

        Assert.True(profile.MatchesExecutablePath("c:\\games\\example\\game.exe"));
        Assert.True(profile.IsAlias("C:\\Games\\Example\\Game.exe"));
    }

    [Theory]
    [InlineData("Example.exe")]
    [InlineData("C:\\Games\\Example\\Launcher.exe")]
    public void Validate_rejects_invalid_or_primary_aliases(string alias)
    {
        var profile = CreateProfile() with { ExecutableAliases = [alias] };

        Assert.Throws<InvalidOperationException>(profile.Validate);
    }

    private static GameProfile CreateProfile() => new(
        "C:\\Games\\Example\\Launcher.exe",
        "Example",
        true,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}
