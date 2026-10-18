using System.ComponentModel;
using System.Diagnostics;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Blackbox.Tests;

public sealed class WindowsRunningApplicationCatalogTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Concurrent_calls_share_one_enumeration_tree_and_native_path_query_per_process()
    {
        var native = new FakeNative();
        native.Windows[102] = Window(102);
        var fallback = new FakeResolver();
        var catalog = Create(native, fallback);

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => catalog.GetRunningApplicationsAsync()));

        Assert.All(results, result => Assert.Equal(2, result.Count));
        Assert.Equal(1, native.Enumerations);
        Assert.Equal(1, native.TreeReads);
        Assert.Equal(1, native.PathQueries);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Unresolved_process_is_negative_cached_without_forced_fallback_per_window()
    {
        var native = new FakeNative { Path = null };
        native.Windows[102] = Window(102);
        var fallback = new FakeResolver();
        var catalog = Create(native, fallback);

        Assert.Empty(await catalog.GetRunningApplicationsAsync());
        Assert.Empty(await catalog.GetRunningApplicationsAsync());

        Assert.Equal(1, native.PathQueries);
        Assert.Equal(1, fallback.Calls);
        Assert.False(fallback.WasForced);
    }

    [Fact]
    public async Task Protected_process_fallback_preserves_both_window_handles()
    {
        var native = new FakeNative { Path = null };
        native.Windows[102] = Window(102);
        var fallback = new FakeResolver { Path = @"C:\Games\game.exe" };

        var applications = await Create(native, fallback).GetRunningApplicationsAsync();

        Assert.Equal(new long[] { 101, 102 }, applications.Select(app => app.WindowHandle));
        Assert.Equal(1, fallback.Calls);
        Assert.Equal("game.exe", fallback.LastName);
    }

    [Fact]
    public async Task Expiry_discovers_new_windows_and_does_not_reuse_a_cached_process_path()
    {
        var time = new DiscoveryTestTimeProvider();
        var native = new FakeNative();
        var catalog = Create(native, time: time);
        _ = await catalog.GetRunningApplicationsAsync();
        native.Path = @"C:\Other\replacement.exe";
        native.Windows[102] = Window(102);

        Assert.Single(await catalog.GetRunningApplicationsAsync());
        time.Advance(WindowsRunningApplicationCatalog.CacheDuration);
        var applications = await catalog.GetRunningApplicationsAsync();

        Assert.Equal(2, applications.Count);
        Assert.All(applications, app => Assert.Equal(native.Path, app.ExecutablePath));
        Assert.Equal(2, native.Enumerations);
        Assert.Equal(2, native.PathQueries);
    }

    [Fact]
    public async Task Cached_entries_refresh_title_foreground_size_and_minimized_state()
    {
        var native = new FakeNative();
        var catalog = Create(native);
        _ = await catalog.GetRunningApplicationsAsync();
        native.Foreground = 0;
        native.Windows[101] = Window() with
        {
            Title = "Changed",
            IsMinimized = true,
            ClientWidth = 0,
            ClientHeight = 0,
            RestoredWidth = 1920,
            RestoredHeight = 1080
        };

        var application = Assert.Single(await catalog.GetRunningApplicationsAsync());

        Assert.Equal("Changed", application.Title);
        Assert.Equal("Changed:GameWindow:game.exe", application.ObsWindowIdentifier);
        Assert.False(application.IsForeground);
        Assert.False(application.DetectionSources.HasFlag(GameDetectionSource.ForegroundWindow));
        Assert.True(application.IsMinimized);
        Assert.False(application.IsCapturable);
        Assert.Equal(1920, application.WindowWidth);
        Assert.Equal(1, native.Enumerations);

        native.Windows[101] = Window() with { ClientWidth = 1600 };
        var restored = Assert.Single(await catalog.GetRunningApplicationsAsync());
        Assert.True(restored.IsCapturable);
        Assert.Equal(1600, restored.WindowWidth);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("pid")]
    [InlineData("class")]
    [InlineData("hidden")]
    [InlineData("cloaked")]
    [InlineData("child")]
    public async Task Cached_entries_reject_closed_or_reused_or_unsafe_windows(string change)
    {
        var native = new FakeNative();
        var catalog = Create(native);
        _ = await catalog.GetRunningApplicationsAsync();
        native.Windows[101] = change switch
        {
            "pid" => Window() with { ProcessId = 43 },
            "class" => Window() with { WindowClass = "Reused" },
            "hidden" => Window() with { Taskbar = new(false, false, false, 0) },
            "cloaked" => Window() with { Taskbar = new(true, true, false, 0) },
            "child" => Window() with { IsTopLevel = false },
            _ => null
        };

        Assert.Empty(await catalog.GetRunningApplicationsAsync());
        Assert.Equal(1, native.Enumerations);
    }

    [Fact]
    public async Task Zero_client_area_is_discoverable_but_not_capturable_even_with_restored_bounds()
    {
        var native = new FakeNative();
        native.Windows[101] = Window() with { ClientWidth = 0, RestoredWidth = 1920, RestoredHeight = 1080 };

        var application = Assert.Single(await Create(native).GetRunningApplicationsAsync());

        Assert.False(application.IsCapturable);
        Assert.False(application.IsMinimized);
    }

    [Fact]
    public async Task Specific_handle_can_discover_a_visible_tool_window_without_full_enumeration()
    {
        var native = new FakeNative();
        native.Windows[102] = Window(102) with
        {
            Taskbar = new(true, false, true, TaskbarWindowCatalogRules.ToolWindowStyle)
        };
        var catalog = Create(native);

        var application = await catalog.GetRunningApplicationAsync(102);

        Assert.NotNull(application);
        Assert.Equal(102, application.WindowHandle);
        Assert.Equal(0, native.Enumerations);
        Assert.Null(await catalog.GetRunningApplicationAsync(999));
        Assert.Equal(0, native.Enumerations);
    }

    [Fact]
    public async Task Full_discovery_keeps_non_game_taskbar_apps_but_does_not_classify_arsenal_as_a_game()
    {
        var native = new FakeNative { Path = @"C:\Steam\steamapps\common\Helldivers 2\HD2Arsenal.exe" };
        native.Tree[42] = new(42, 50, "HD2Arsenal.exe");
        native.Tree[50] = new(50, 0, "steam.exe");

        var application = Assert.Single(await Create(native).GetRunningApplicationsAsync());

        Assert.Equal("HD2Arsenal.exe", application.ExecutableName);
        Assert.Equal(GameDetectionSource.ForegroundWindow, application.DetectionSources);
    }

    [Fact]
    public async Task Cancellation_does_not_publish_a_partial_cache_or_leave_the_gate_locked()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new FakeNative { OnEnumerate = cancellation.Cancel };
        var catalog = Create(native);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.GetRunningApplicationsAsync(cancellation.Token));
        native.OnEnumerate = null;

        Assert.Single(await catalog.GetRunningApplicationsAsync());
        Assert.Equal(2, native.Enumerations);
    }

    [Fact]
    public async Task Enumeration_failure_revalidates_old_entries_and_is_throttled()
    {
        var time = new DiscoveryTestTimeProvider();
        var native = new FakeNative();
        var catalog = Create(native, time: time);
        _ = await catalog.GetRunningApplicationsAsync();
        time.Advance(WindowsRunningApplicationCatalog.CacheDuration);
        native.OnEnumerate = () => throw new Win32Exception();
        native.Windows[101] = null;

        Assert.Empty(await catalog.GetRunningApplicationsAsync());
        Assert.Empty(await catalog.GetRunningApplicationsAsync());
        Assert.Equal(2, native.Enumerations);
    }

    [Fact]
    public async Task Native_windows_smoke_test_returns_exact_unique_handles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var catalog = new WindowsRunningApplicationCatalog(
            new SteamProcessExecutablePathResolver(NullLogger<SteamProcessExecutablePathResolver>.Instance),
            NullLogger<WindowsRunningApplicationCatalog>.Instance);
        var timer = Stopwatch.StartNew();
        var first = await catalog.GetRunningApplicationsAsync();
        var firstMilliseconds = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var cached = await catalog.GetRunningApplicationsAsync();
        output.WriteLine($"Native discovery: {first.Count} windows, cold={firstMilliseconds:F1}ms, cached={timer.Elapsed.TotalMilliseconds:F1}ms.");

        Assert.All(first, application => Assert.NotEqual(0, application.WindowHandle));
        Assert.Equal(cached.Count, cached.Select(app => (app.ProcessId, app.WindowHandle)).Distinct().Count());
    }

    private static WindowsRunningApplicationCatalog Create(
        FakeNative native, FakeResolver? fallback = null, DiscoveryTestTimeProvider? time = null) =>
        new(fallback ?? new FakeResolver(), NullLogger<WindowsRunningApplicationCatalog>.Instance,
            native, time ?? new DiscoveryTestTimeProvider());

    private static WindowCatalogSnapshot Window(long handle = 101) =>
        new(handle, 42, "Game", "GameWindow", new(true, false, false, 0), 1280, 720);

    private sealed class FakeNative : IWindowCatalogNativeAccess
    {
        public bool IsSupported => true;
        public long Foreground { get; set; } = 101;
        public string? Path { get; set; } = @"C:\Games\game.exe";
        public Dictionary<long, WindowCatalogSnapshot?> Windows { get; } = new() { [101] = Window() };
        public Dictionary<int, ProcessTreeEntry> Tree { get; } = new() { [42] = new(42, 0, "game.exe") };
        public int Enumerations { get; private set; }
        public int TreeReads { get; private set; }
        public int PathQueries { get; private set; }
        public Action? OnEnumerate { get; set; }
        public long GetForegroundWindow() => Foreground;
        public WindowCatalogSnapshot? ReadWindow(long handle) => Windows.GetValueOrDefault(handle);
        public IReadOnlyList<long> EnumerateWindows(CancellationToken cancellationToken)
        {
            Enumerations++;
            OnEnumerate?.Invoke();
            return Windows.Keys.ToArray();
        }

        public string? ReadExecutablePath(int processId)
        {
            PathQueries++;
            return Path;
        }

        public IReadOnlyDictionary<int, ProcessTreeEntry> ReadProcessTree(CancellationToken cancellationToken)
        {
            TreeReads++;
            return Tree;
        }
    }

    private sealed class FakeResolver : IProcessExecutablePathResolver
    {
        public string? Path { get; init; }
        public int Calls { get; private set; }
        public bool WasForced { get; private set; }
        public string? LastName { get; private set; }

        public string? Resolve(int processId, string executableName, bool forceRefresh = false)
        {
            Calls++;
            WasForced |= forceRefresh;
            LastName = executableName;
            return Path;
        }
    }
}

internal sealed class DiscoveryTestTimeProvider : TimeProvider
{
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
}
