using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class GameCandidateSelectorTests
{
    [Fact]
    public void Select_detects_a_foreground_game_from_its_steam_library_path()
    {
        var foreground = new ForegroundProcessSnapshot(
            50,
            "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Example\\Example.exe",
            "Example.exe",
            "Example Game",
            "ExampleWindow");

        var result = GameCandidateSelector.Select(foreground, new Dictionary<int, ProcessTreeEntry>());

        Assert.NotNull(result);
        Assert.True(result.DetectionSources.HasFlag(GameDetectionSource.ForegroundWindow));
        Assert.True(result.DetectionSources.HasFlag(GameDetectionSource.SteamLibrary));
        Assert.Equal("Example Game:ExampleWindow:Example.exe", result.ObsWindowIdentifier);
    }

    [Fact]
    public void Select_detects_a_game_with_a_steam_ancestor_outside_the_default_library()
    {
        var foreground = new ForegroundProcessSnapshot(
            50,
            "D:\\Games\\Example.exe",
            "Example.exe",
            "Example Game",
            "ExampleWindow");
        IReadOnlyDictionary<int, ProcessTreeEntry> tree = new Dictionary<int, ProcessTreeEntry>
        {
            [50] = new(50, 20, "Example.exe"),
            [20] = new(20, 1, "steam.exe")
        };

        var result = GameCandidateSelector.Select(foreground, tree);

        Assert.NotNull(result);
        Assert.True(result.DetectionSources.HasFlag(GameDetectionSource.SteamProcessTree));
    }

    [Theory]
    [InlineData("C:\\Windows\\explorer.exe", "explorer.exe")]
    [InlineData("D:\\Tools\\Editor.exe", "Editor.exe")]
    public void Select_ignores_shells_and_unconfirmed_non_steam_apps(string path, string executableName)
    {
        var foreground = new ForegroundProcessSnapshot(50, path, executableName, "Window", "WindowClass");

        var result = GameCandidateSelector.Select(foreground, new Dictionary<int, ProcessTreeEntry>());

        Assert.Null(result);
    }
}
