using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Blackbox.App;

public partial class GameProfilesPage
{
    internal async Task ValidatePixelScrollingAsync()
    {
        foreach (var list in new[] { RunningApplicationsList, RememberedGamesList, AliasesList })
        {
            var source = list.ItemsSource;
            var selection = list.SelectedItem;
            try
            {
                list.ItemsSource = Enumerable.Range(1, 200).Select(index => new
                {
                    Title = $"Test window {index}", DisplayName = $"Test game {index}",
                    ExecutableName = "test.exe", ExecutablePath = @"C:\Tests\test.exe",
                    ActivitySummary = "1920 x 1080", CaptureSummary = "Remembered",
                    PreferenceSummary = "Auto | Global profile"
                }).ToArray();
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
                if (!VirtualizingPanel.GetIsVirtualizing(list) ||
                    VirtualizingPanel.GetScrollUnit(list) != ScrollUnit.Pixel)
                    throw new InvalidOperationException($"{list.Name} must use virtualized pixel scrolling.");
                var viewer = FindScrollViewer(list) ?? throw new InvalidOperationException($"{list.Name} has no scroll viewer.");
                if (!viewer.CanContentScroll || viewer.ScrollableHeight <= 13)
                    throw new InvalidOperationException($"{list.Name} did not provide a scrollable virtualized list.");
                viewer.ScrollToVerticalOffset(13);
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
                if (Math.Abs(viewer.VerticalOffset - 13) > 0.5)
                    throw new InvalidOperationException($"{list.Name} snapped instead of scrolling 13 pixels.");
                if (list.ItemContainerGenerator.ContainerFromIndex(199) is not null)
                    throw new InvalidOperationException($"{list.Name} realized offscreen rows instead of virtualizing.");
            }
            finally
            {
                list.ItemsSource = source;
                list.SelectedItem = selection;
            }
        }

        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
        if (ProfileSettingsScrollViewer.CanContentScroll || ProfileSettingsScrollViewer.ScrollableHeight <= 13)
            throw new InvalidOperationException("Game settings must provide pixel scrolling.");
        ProfileSettingsScrollViewer.ScrollToVerticalOffset(13);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
        if (Math.Abs(ProfileSettingsScrollViewer.VerticalOffset - 13) > 0.5)
            throw new InvalidOperationException("Game settings did not scroll 13 pixels.");
        ProfileSettingsScrollViewer.ScrollToTop();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}
