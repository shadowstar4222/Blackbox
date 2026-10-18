using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class TaskbarWindowCatalogRulesTests
{
    [Fact]
    public void IsEligible_accepts_a_normal_visible_taskbar_window()
    {
        var window = new TaskbarWindowSnapshot(
            IsVisible: true,
            IsCloaked: false,
            HasOwner: false,
            ExtendedStyle: 0);

        Assert.True(TaskbarWindowCatalogRules.IsEligible(window));
    }

    [Fact]
    public void IsEligible_rejects_hidden_cloaked_tool_and_owned_windows()
    {
        Assert.False(TaskbarWindowCatalogRules.IsEligible(new(false, false, false, 0)));
        Assert.False(TaskbarWindowCatalogRules.IsEligible(new(true, true, false, 0)));
        Assert.False(TaskbarWindowCatalogRules.IsEligible(new(
            true,
            false,
            false,
            TaskbarWindowCatalogRules.ToolWindowStyle)));
        Assert.False(TaskbarWindowCatalogRules.IsEligible(new(true, false, true, 0)));
    }

    [Fact]
    public void IsEligible_accepts_an_explicit_app_window()
    {
        var window = new TaskbarWindowSnapshot(
            IsVisible: true,
            IsCloaked: false,
            HasOwner: true,
            ExtendedStyle: TaskbarWindowCatalogRules.AppWindowStyle |
                           TaskbarWindowCatalogRules.ToolWindowStyle);

        Assert.True(TaskbarWindowCatalogRules.IsEligible(window));
    }

    [Fact]
    public void IsLikelyVisibleGameSurface_recovers_a_large_steam_game_window()
    {
        var nonstandardWindow = new TaskbarWindowSnapshot(
            IsVisible: true,
            IsCloaked: false,
            HasOwner: true,
            ExtendedStyle: TaskbarWindowCatalogRules.ToolWindowStyle);

        var result = TaskbarWindowCatalogRules.IsLikelyVisibleGameSurface(
            nonstandardWindow,
            hasWindowTitle: true,
            width: 1920,
            height: 1080,
            GameDetectionSource.SteamLibrary | GameDetectionSource.SteamProcessTree);

        Assert.True(result);
    }

    [Theory]
    [InlineData(GameDetectionSource.None, 1920, 1080)]
    [InlineData(GameDetectionSource.SteamLibrary, 215, 28)]
    public void IsLikelyVisibleGameSurface_rejects_non_games_and_small_utility_windows(
        GameDetectionSource detectionSources,
        int width,
        int height)
    {
        var window = new TaskbarWindowSnapshot(
            IsVisible: true,
            IsCloaked: false,
            HasOwner: true,
            ExtendedStyle: TaskbarWindowCatalogRules.ToolWindowStyle);

        var result = TaskbarWindowCatalogRules.IsLikelyVisibleGameSurface(
            window,
            hasWindowTitle: true,
            width,
            height,
            detectionSources);

        Assert.False(result);
    }

    [Fact]
    public void Order_preserves_multiple_windows_from_the_same_application()
    {
        var applications = new[]
        {
            CreateApplication(10, "C:\\Apps\\browser.exe", "Second window", isForeground: false),
            CreateApplication(10, "C:\\Apps\\browser.exe", "First window", isForeground: true)
        };

        var result = TaskbarWindowCatalogRules.Order(applications);

        Assert.Equal(2, result.Count);
        Assert.Equal("First window", result[0].Title);
        Assert.Equal("Second window", result[1].Title);
    }

    [Fact]
    public void Order_deduplicates_repeated_window_snapshots_and_keeps_foreground_state()
    {
        var applications = new[]
        {
            CreateApplication(10, "C:\\Games\\helldivers2.exe", "HELLDIVERS 2", isForeground: false),
            CreateApplication(10, "C:\\Games\\helldivers2.exe", "HELLDIVERS 2", isForeground: true)
        };

        var result = TaskbarWindowCatalogRules.Order(applications);

        var application = Assert.Single(result);
        Assert.True(application.IsForeground);
    }

    [Theory]
    [InlineData(1280, 720, 1920, 1080, 1280, 720)]
    [InlineData(0, 0, 1920, 1080, 1920, 1080)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(0, 0, -1, -1, 0, 0)]
    public void ResolveWindowSize_uses_restored_bounds_for_minimized_windows(
        int clientWidth,
        int clientHeight,
        int restoredWidth,
        int restoredHeight,
        int expectedWidth,
        int expectedHeight)
    {
        var result = TaskbarWindowCatalogRules.ResolveWindowSize(
            clientWidth,
            clientHeight,
            restoredWidth,
            restoredHeight);

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
    }

    [Fact]
    public void Order_keeps_identically_titled_windows_with_distinct_handles()
    {
        var application = CreateApplication(10, @"C:\Apps\browser.exe", "Same title", false);
        var result = TaskbarWindowCatalogRules.Order([
            application with { WindowHandle = 101 },
            application with { WindowHandle = 102 }
        ]);

        Assert.Equal(new long[] { 101, 102 }, result.Select(app => app.WindowHandle));
    }

    [Fact]
    public void Order_deduplicates_the_same_handle_after_a_title_change()
    {
        var application = CreateApplication(10, @"C:\Apps\browser.exe", "Old title", false) with
        {
            WindowHandle = 101
        };
        var result = TaskbarWindowCatalogRules.Order([
            application,
            application with { Title = "New title", ObsWindowIdentifier = "New:Class:browser.exe", IsForeground = true }
        ]);

        Assert.Equal("New title", Assert.Single(result).Title);
    }

    [Fact]
    public void Capture_target_preserves_handle_and_state_without_changing_executable_identity()
    {
        var application = CreateApplication(10, @"C:\Apps\browser.exe", "Window", false) with
        {
            WindowHandle = 101,
            IsMinimized = true,
            IsCapturable = false
        };

        var target = application.ToCaptureTarget(GameDetectionSource.ConfiguredExecutable);

        Assert.Equal(101, target.WindowHandle);
        Assert.True(target.IsMinimized);
        Assert.False(target.IsCapturable);
        Assert.Equal(application.Identity, target.Identity);
        Assert.Throws<InvalidOperationException>(target.Validate);
    }

    [Fact]
    public void Capture_source_identity_distinguishes_handles_and_processes_not_dynamic_titles()
    {
        var target = CreateApplication(10, @"C:\Apps\browser.exe", "Window", false)
            .ToCaptureTarget() with { WindowHandle = 101 };

        Assert.NotEqual(target.CaptureSourceIdentity, (target with { WindowHandle = 102 }).CaptureSourceIdentity);
        Assert.NotEqual(target.CaptureSourceIdentity, (target with { ProcessId = 11 }).CaptureSourceIdentity);
        Assert.Equal(target.CaptureSourceIdentity, (target with { ObsWindowIdentifier = "Changed:Class:browser.exe" }).CaptureSourceIdentity);
        Assert.Equal(target.Identity, (target with { WindowHandle = 102 }).Identity);
    }

    private static RunningApplication CreateApplication(
        int processId,
        string executablePath,
        string title,
        bool isForeground) =>
        new(
            processId,
            executablePath,
            Path.GetFileName(executablePath),
            title,
            $"{title}:WindowClass:{Path.GetFileName(executablePath)}",
            1280,
            720,
            isForeground,
            GameDetectionSource.ForegroundWindow);
}
