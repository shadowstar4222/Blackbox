using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class RememberedGameProcessDetectorTests
{
    [Fact]
    public async Task DetectAsync_selects_an_enabled_remembered_game_instead_of_an_unapproved_foreground_app()
    {
        var rememberedPath = "C:\\Games\\Remembered\\Remembered.exe";
        var catalog = new StubRunningApplicationCatalog(
        [
            Application("C:\\Games\\Unknown\\Unknown.exe", foreground: true),
            Application(rememberedPath, foreground: false, width: 2560, height: 1440)
        ]);
        var repository = new StubGameProfileRepository(
        [
            new GameProfile(
                rememberedPath,
                "My Remembered Game",
                true,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)
        ]);
        var detector = new WindowsGameProcessDetector(
            catalog,
            repository,
            NullLogger<WindowsGameProcessDetector>.Instance);

        var target = await detector.DetectAsync();

        Assert.NotNull(target);
        Assert.Equal("My Remembered Game", target.Title);
        Assert.Equal(rememberedPath, target.ExecutablePath);
        Assert.Equal(2560, target.WindowWidth);
        Assert.Equal(1440, target.WindowHeight);
        Assert.True(target.DetectionSources.HasFlag(GameDetectionSource.ConfiguredExecutable));
    }

    [Fact]
    public async Task DetectAsync_ignores_disabled_and_unknown_games()
    {
        var path = "C:\\Games\\Example\\Example.exe";
        var detector = new WindowsGameProcessDetector(
            new StubRunningApplicationCatalog([Application(path, foreground: true)]),
            new StubGameProfileRepository(
            [
                new GameProfile(path, "Example", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]),
            NullLogger<WindowsGameProcessDetector>.Instance);

        Assert.Null(await detector.DetectAsync());
    }

    private static RunningApplication Application(
        string executablePath,
        bool foreground,
        int width = 1920,
        int height = 1080)
    {
        var executableName = Path.GetFileName(executablePath);
        return new RunningApplication(
            42,
            executablePath,
            executableName,
            executableName,
            $"{executableName}:WindowClass:{executableName}",
            width,
            height,
            foreground,
            foreground ? GameDetectionSource.ForegroundWindow : GameDetectionSource.None);
    }

    private sealed class StubRunningApplicationCatalog(
        IReadOnlyList<RunningApplication> applications) : IRunningApplicationCatalog
    {
        public Task<IReadOnlyList<RunningApplication>> GetRunningApplicationsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(applications);
    }

    private sealed class StubGameProfileRepository(
        IReadOnlyList<GameProfile> profiles) : IGameProfileRepository
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(profiles);

        public Task UpsertAsync(GameProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string executablePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
