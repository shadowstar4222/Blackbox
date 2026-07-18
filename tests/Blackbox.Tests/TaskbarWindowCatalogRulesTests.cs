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
            IsRootOwnerLastActivePopup: true,
            HasOwner: false,
            ExtendedStyle: 0);

        Assert.True(TaskbarWindowCatalogRules.IsEligible(window));
    }

    [Fact]
    public void IsEligible_rejects_hidden_cloaked_tool_and_owned_windows()
    {
        Assert.False(TaskbarWindowCatalogRules.IsEligible(new(false, false, true, false, 0)));
        Assert.False(TaskbarWindowCatalogRules.IsEligible(new(true, true, true, false, 0)));
        Assert.False(TaskbarWindowCatalogRules.IsEligible(new(
            true,
            false,
            true,
            false,
            TaskbarWindowCatalogRules.ToolWindowStyle)));
        Assert.False(TaskbarWindowCatalogRules.IsEligible(new(true, false, true, true, 0)));
        Assert.False(TaskbarWindowCatalogRules.IsEligible(new(true, false, false, false, 0)));
    }

    [Fact]
    public void IsEligible_accepts_an_explicit_app_window()
    {
        var window = new TaskbarWindowSnapshot(
            IsVisible: true,
            IsCloaked: false,
            IsRootOwnerLastActivePopup: false,
            HasOwner: true,
            ExtendedStyle: TaskbarWindowCatalogRules.AppWindowStyle |
                           TaskbarWindowCatalogRules.ToolWindowStyle);

        Assert.True(TaskbarWindowCatalogRules.IsEligible(window));
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

    [Theory]
    [InlineData(1280, 720, 1920, 1080, 1280, 720)]
    [InlineData(0, 0, 1920, 1080, 1920, 1080)]
    [InlineData(0, 0, 0, 0, 1, 1)]
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
