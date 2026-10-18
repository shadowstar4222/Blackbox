using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Blackbox.Domain;

namespace Blackbox.App;

public partial class DiagnosticsPage
{
    internal async Task ValidateScrollingAsync(string directory, double width, Func<string, Task> saveImage)
    {
        var entries = _allEntries;
        var category = CategoryComboBox.SelectedItem;
        try
        {
            DiagnosticsTabs.SelectedIndex = 0;
            _allEntries = Enumerable.Range(0, 300).Select(index => new DiagnosticLogEntry(
                DateTimeOffset.UtcNow.AddSeconds(-index), DiagnosticCategory.Recording, DiagnosticSeverity.Information,
                index == 0 ? string.Join('\n', Enumerable.Range(1, 150).Select(line => $"Long diagnostic entry, line {line}: capture output and microphone details."))
                    : $"Event {index}: " + string.Join(' ', Enumerable.Repeat("Variable-height diagnostic evidence.", index % 9 + 1)), "ScrollFixture")).ToArray();
            CategoryComboBox.SelectedItem = "All";
            ApplyFilter();
            await SettleScrollLayoutAsync();
            var viewer = FindDiagnosticVisual<ScrollViewer>(LogListBox) ?? throw new InvalidOperationException("Missing diagnostic scroll viewer.");
            viewer.ScrollToTop();
            await SettleScrollLayoutAsync();
            var first = (FrameworkElement)LogListBox.ItemContainerGenerator.ContainerFromIndex(0);
            if (first.ActualHeight <= viewer.ViewportHeight)
                throw new InvalidOperationException("The diagnostic fixture did not contain a row taller than the viewport.");
            var initialTop = first.TransformToAncestor(viewer).Transform(new Point()).Y;
            viewer.ScrollToVerticalOffset(13);
            await SettleScrollLayoutAsync();
            await saveImage($"diagnostics-long-row-{width}");
            first = LogListBox.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            if (first is null || Math.Abs(initialTop - first.TransformToAncestor(viewer).Transform(new Point()).Y - 13) > 1)
                throw new InvalidOperationException("Diagnostics skipped an entire tall event instead of scrolling its contents by 13 pixels.");
            if (!viewer.CanContentScroll || VirtualizingPanel.GetScrollUnit(LogListBox) != ScrollUnit.Pixel ||
                !VirtualizingPanel.GetIsVirtualizing(LogListBox) || LogListBox.ItemContainerGenerator.ContainerFromIndex(299) is not null)
                throw new InvalidOperationException("Diagnostic pixel scrolling lost virtualization.");
            var beforeWheel = viewer.VerticalOffset;
            viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            await SettleScrollLayoutAsync();
            if (viewer.VerticalOffset <= beforeWheel || viewer.VerticalOffset - beforeWheel >= viewer.ViewportHeight)
                throw new InvalidOperationException("Diagnostic wheel scrolling jumped a full page or did not move.");
            var bar = FindDiagnosticVisual<ScrollBar>(viewer) ?? throw new InvalidOperationException("Missing diagnostic scrollbar.");
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            if (Math.Abs(track.Maximum - bar.Maximum) > 1 || Math.Abs(bar.Maximum - viewer.ScrollableHeight) > 1)
                throw new InvalidOperationException("Diagnostic scrollbar range is disconnected from its content.");
            var beforeDrag = viewer.VerticalOffset;
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 30) { RoutedEvent = Thumb.DragDeltaEvent });
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 30, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await SettleScrollLayoutAsync();
            if (viewer.VerticalOffset <= beforeDrag) throw new InvalidOperationException("Dragging the diagnostic scrollbar did not move its content.");
            viewer.ScrollToBottom();
            await SettleScrollLayoutAsync();
            viewer.ScrollToBottom();
            await SettleScrollLayoutAsync();
            if (LogListBox.ItemContainerGenerator.ContainerFromIndex(299) is not FrameworkElement last ||
                last.TransformToAncestor(viewer).Transform(new Point(0, last.ActualHeight)).Y > viewer.ActualHeight + 1)
                throw new InvalidOperationException("The final diagnostic entry cannot be reached.");
            await saveImage($"diagnostics-list-end-{width}");
            CategoryComboBox.SelectedItem = "Recovery";
            await SettleScrollLayoutAsync();
            if (LogListBox.Items.Count != 0 || viewer.VerticalOffset != 0 || viewer.ScrollableHeight != 0)
                throw new InvalidOperationException("Filtering to no events retained an invalid scrollbar offset.");
            _allEntries = _allEntries.Skip(1).Take(3).Select(entry => entry with { Message = "Short event." }).ToArray();
            CategoryComboBox.SelectedItem = "All";
            await SettleScrollLayoutAsync();
            if (viewer.ScrollableHeight != 0 || viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                throw new InvalidOperationException("Short diagnostic lists retained an unnecessary scrollbar.");
            File.WriteAllText(Path.Combine(directory, $"diagnostics-scroll-{width}.json"), JsonSerializer.Serialize(new
            {
                PixelOffset = 13, TallRowContentsReachable = true, WheelScrolling = true, ThumbDragging = true,
                VirtualizationPreserved = true, FinalEntryReachable = true, EmptyAndShortFiltersReset = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            _allEntries = entries;
            CategoryComboBox.SelectedItem = category;
            ApplyFilter();
        }
    }

    private async Task SettleScrollLayoutAsync()
    {
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
    }

    private static T? FindDiagnosticVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDiagnosticVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
