using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class RememberedGameProcessDetectorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");

    [Fact]
    public async Task DetectAsync_selects_an_enabled_remembered_game_instead_of_an_unapproved_foreground_app()
    {
        var rememberedPath = "C:\\Games\\Remembered\\Remembered.exe";
        var detector = CreateDetector(
            [
                Application("C:\\Games\\Unknown\\Unknown.exe", foreground: true),
                Application(rememberedPath, foreground: false, width: 2560, height: 1440)
            ],
            new StubGameProfileRepository([Profile(rememberedPath, "My Remembered Game")]));

        var target = await detector.DetectAsync();

        Assert.NotNull(target);
        Assert.Equal("My Remembered Game", target.Title);
        Assert.Equal(rememberedPath, target.ExecutablePath);
        Assert.Equal(2560, target.WindowWidth);
        Assert.Equal(1440, target.WindowHeight);
        Assert.True(target.DetectionSources.HasFlag(GameDetectionSource.ConfiguredExecutable));
    }

    [Fact]
    public async Task DetectAsync_matches_a_saved_alias_and_applies_audio_preference()
    {
        var launcherPath = "C:\\Games\\Example\\Launcher.exe";
        var gamePath = "C:\\Games\\Example\\Game.exe";
        var profile = Profile(launcherPath, "Example") with
        {
            ExecutableAliases = [gamePath],
            CaptureGameAudio = false
        };
        var detector = CreateDetector(
            [Application(gamePath, foreground: true)],
            new StubGameProfileRepository([profile]));

        var target = await detector.DetectAsync();

        Assert.NotNull(target);
        Assert.Equal(gamePath, target.ExecutablePath);
        Assert.False(target.CaptureGameAudio);
        Assert.True(target.DetectionSources.HasFlag(GameDetectionSource.ExecutableAlias));
    }

    [Fact]
    public async Task DetectAsync_learns_launcher_child_and_persists_it_as_an_alias()
    {
        var launcherPath = "C:\\Games\\Example\\Launcher.exe";
        var gamePath = "C:\\Games\\Example\\Game.exe";
        var repository = new StubGameProfileRepository([Profile(launcherPath, "Example")]);
        var child = Application(gamePath, foreground: true) with
        {
            AncestorExecutableNames = ["Launcher.exe", "steam.exe"]
        };
        var detector = CreateDetector([child], repository);

        var confirmingTarget = await detector.DetectAsync();
        Assert.NotNull(confirmingTarget);
        Assert.Empty((Assert.Single(await repository.GetAllAsync())).ExecutableAliases);

        var target = await detector.DetectAsync();

        Assert.NotNull(target);
        Assert.True(target.DetectionSources.HasFlag(GameDetectionSource.LauncherHandoff));
        Assert.True(target.DetectionSources.HasFlag(GameDetectionSource.ExecutableAlias));
        var stored = Assert.Single(await repository.GetAllAsync());
        Assert.Contains(gamePath, stored.ExecutableAliases, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Now, stored.UpdatedAt);
    }

    [Fact]
    public async Task DetectAsync_does_not_follow_launcher_when_profile_disables_handoff()
    {
        var launcherPath = "C:\\Games\\Example\\Launcher.exe";
        var child = Application("C:\\Games\\Example\\Game.exe", foreground: true) with
        {
            AncestorExecutableNames = ["Launcher.exe"]
        };
        var detector = CreateDetector(
            [child],
            new StubGameProfileRepository(
            [
                Profile(launcherPath, "Example") with { FollowLauncherHandoff = false }
            ]));

        Assert.Null(await detector.DetectAsync());
    }

    [Fact]
    public async Task DetectAsync_prefers_gpu_active_alias_and_marks_corroboration()
    {
        var launcherPath = "C:\\Games\\Example\\Launcher.exe";
        var gamePath = "C:\\Games\\Example\\Game.exe";
        var profile = Profile(launcherPath, "Example") with
        {
            ExecutableAliases = [gamePath],
            PreferGpuActivity = true
        };
        var gpu = new StubGpuActivityProbe(new GpuActivitySnapshot(
            true,
            new Dictionary<int, double> { [84] = 37.5 }));
        var detector = CreateDetector(
            [
                Application(launcherPath, foreground: true, processId: 42),
                Application(gamePath, foreground: false, processId: 84)
            ],
            new StubGameProfileRepository([profile]),
            gpu);

        var target = await detector.DetectAsync();

        Assert.NotNull(target);
        Assert.Equal(84, target.ProcessId);
        Assert.True(target.DetectionSources.HasFlag(GameDetectionSource.GpuActivity));
        Assert.Equal(1, gpu.CallCount);
    }

    [Fact]
    public async Task DetectAsync_ignores_disabled_and_unknown_games()
    {
        var path = "C:\\Games\\Example\\Example.exe";
        var detector = CreateDetector(
            [Application(path, foreground: true)],
            new StubGameProfileRepository([Profile(path, "Example", enabled: false)]));

        Assert.Null(await detector.DetectAsync());
    }

    [Fact]
    public async Task DetectAsync_keeps_the_preferred_running_game_selected_over_the_foreground_game()
    {
        var firstPath = "C:\\Games\\First\\First.exe";
        var secondPath = "C:\\Games\\Second\\Second.exe";
        var detector = CreateDetector(
            [
                Application(firstPath, foreground: true, processId: 42),
                Application(secondPath, foreground: false, processId: 84)
            ],
            new StubGameProfileRepository(
            [
                Profile(firstPath, "First Game"),
                Profile(secondPath, "Second Game")
            ]),
            selection: new GameCaptureSelection(secondPath, secondPath, "Second Game"));

        var target = await detector.DetectAsync();

        Assert.NotNull(target);
        Assert.Equal(84, target.ProcessId);
        Assert.Equal("Second Game", target.Title);
    }

    [Fact]
    public async Task DetectAsync_falls_back_when_the_preferred_game_is_not_running()
    {
        var runningPath = "C:\\Games\\Running\\Running.exe";
        var closedPath = "C:\\Games\\Closed\\Closed.exe";
        var detector = CreateDetector(
            [Application(runningPath, foreground: true)],
            new StubGameProfileRepository(
            [
                Profile(runningPath, "Running Game"),
                Profile(closedPath, "Closed Game")
            ]),
            selection: new GameCaptureSelection(closedPath, closedPath, "Closed Game"));

        var target = await detector.DetectAsync();

        Assert.NotNull(target);
        Assert.Equal("Running Game", target.Title);
    }

    private static WindowsGameProcessDetector CreateDetector(
        IReadOnlyList<RunningApplication> applications,
        StubGameProfileRepository repository,
        StubGpuActivityProbe? gpu = null,
        GameCaptureSelection? selection = null) =>
        new(
            new StubRunningApplicationCatalog(applications),
            repository,
            new StubGameCaptureSelectionStore(selection),
            gpu ?? new StubGpuActivityProbe(),
            new GpuActivityOptions(),
            new FixedClock(Now),
            NullLogger<WindowsGameProcessDetector>.Instance);

    private sealed class StubGameCaptureSelectionStore(GameCaptureSelection? selection)
        : IGameCaptureSelectionStore
    {
        public GameCaptureSelection? Current { get; private set; } = selection;
        public void Save(GameCaptureSelection value) => Current = value;
        public void Clear() => Current = null;
    }

    private static GameProfile Profile(string path, string name, bool enabled = true) =>
        new(path, name, enabled, Now.AddDays(-1), Now.AddDays(-1));

    private static RunningApplication Application(
        string executablePath,
        bool foreground,
        int processId = 42,
        int width = 1920,
        int height = 1080)
    {
        var executableName = Path.GetFileName(executablePath);
        return new RunningApplication(
            processId,
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

    private sealed class StubGpuActivityProbe(GpuActivitySnapshot? snapshot = null) : IGpuActivityProbe
    {
        public int CallCount { get; private set; }

        public Task<GpuActivitySnapshot> SampleAsync(
            IReadOnlyCollection<int> processIds,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(snapshot ?? new GpuActivitySnapshot(
                true,
                new Dictionary<int, double>()));
        }
    }

    private sealed class StubGameProfileRepository(IEnumerable<GameProfile> profiles) : IGameProfileRepository
    {
        private readonly List<GameProfile> _profiles = [.. profiles];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameProfile>>(_profiles.ToArray());

        public Task UpsertAsync(GameProfile profile, CancellationToken cancellationToken = default)
        {
            var index = _profiles.FindIndex(existing => existing.Identity == profile.Identity);
            if (index >= 0)
            {
                _profiles[index] = profile;
            }
            else
            {
                _profiles.Add(profile);
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string executablePath, CancellationToken cancellationToken = default)
        {
            _profiles.RemoveAll(profile => profile.Identity == Path.GetFullPath(executablePath).ToUpperInvariant());
            return Task.CompletedTask;
        }
    }
}
