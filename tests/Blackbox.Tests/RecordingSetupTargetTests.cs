using Blackbox.App;
using Blackbox.Domain;

namespace Blackbox.Tests;

public sealed class RecordingSetupTargetTests
{
    [Fact]
    public async Task Temporary_target_uses_current_dimensions_without_reading_or_writing_game_selection()
    {
        var selected = Window();
        var catalog = new Catalog { Current = selected with { WindowWidth = 900, WindowHeight = 600 } };
        var selection = new Selection();
        var resolver = new ManualCaptureTargetResolver(null!, selection, null!, catalog, null!);
        resolver.SetTemporaryTarget(selected.ToCaptureTarget());
        var result = await resolver.ResolveAsync();
        Assert.NotNull(result);
        Assert.Equal(900, result.WindowWidth);
        Assert.Equal(600, result.WindowHeight);
        Assert.Equal(0, selection.Reads);
        Assert.Equal(0, selection.Writes);
        resolver.SetTemporaryTarget(null);
        Assert.Null(await resolver.ResolveAsync());
        Assert.Equal(1, selection.Reads);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("pid-reused")]
    [InlineData("exe-changed")]
    [InlineData("minimized")]
    [InlineData("not-capturable")]
    [InlineData("handle-changed")]
    public async Task Invalid_temporary_target_never_falls_back_to_another_game(string condition)
    {
        var window = Window();
        var catalog = new Catalog { Current = condition switch
        {
            "closed" => null,
            "pid-reused" => window with { ProcessId = 99 },
            "exe-changed" => window with { ExecutablePath = Path.GetFullPath("different.exe") },
            "minimized" => window with { IsMinimized = true },
            "not-capturable" => window with { IsCapturable = false },
            _ => window with { WindowHandle = 11 }
        } };
        var selection = new Selection();
        var resolver = new ManualCaptureTargetResolver(null!, selection, null!, catalog, null!);
        resolver.SetTemporaryTarget(window.ToCaptureTarget());
        Assert.Null(await resolver.ResolveAsync());
        Assert.Equal(0, selection.Reads);
        Assert.Equal(0, selection.Writes);
    }

    [Fact]
    public async Task Temporary_target_preserves_explicit_audio_choice()
    {
        var window = Window();
        var resolver = new ManualCaptureTargetResolver(null!, new Selection(), null!, new Catalog { Current = window }, null!);
        resolver.SetTemporaryTarget(window.ToCaptureTarget() with { CaptureGameAudio = false });
        Assert.False((await resolver.ResolveAsync())!.CaptureGameAudio);
    }

    private static RunningApplication Window() => new(7, Path.GetFullPath("Blackbox.SetupTarget.exe"),
        "Blackbox.SetupTarget.exe", "Blackbox Recording Setup", "setup:class:exe", 1280, 720, false,
        GameDetectionSource.ConfiguredExecutable) { WindowHandle = 10 };

    private sealed class Catalog : IRunningApplicationCatalog
    {
        internal RunningApplication? Current { get; init; }
        public Task<IReadOnlyList<RunningApplication>> GetRunningApplicationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunningApplication>>(Current is null ? [] : [Current]);
    }

    private sealed class Selection : IGameCaptureSelectionStore
    {
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        public GameCaptureSelection? Current { get { Reads++; return null; } }
        public void Save(GameCaptureSelection selection) => Writes++;
        public void Clear() => Writes++;
    }
}
