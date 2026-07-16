using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

public partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsService _diagnostics;
    private readonly ILogger<DiagnosticsWindow> _logger;
    private readonly ObservableCollection<DiagnosticLogListItem> _visibleEntries = [];
    private IReadOnlyList<DiagnosticLogEntry> _allEntries = [];
    private bool _isBusy;

    public DiagnosticsWindow(
        DiagnosticsService diagnostics,
        ILogger<DiagnosticsWindow> logger)
    {
        _diagnostics = diagnostics;
        _logger = logger;
        InitializeComponent();
        LogListBox.ItemsSource = _visibleEntries;
        CategoryComboBox.ItemsSource = new[] { "All", "Recording", "Detection", "Export", "Recovery", "System" };
        CategoryComboBox.SelectedIndex = 0;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => OpenDirectory(_diagnostics.LogDirectory);

    private void OpenBackupsButton_Click(object sender, RoutedEventArgs e) => OpenDirectory(_diagnostics.RecoveryBackupDirectory);

    private async Task RefreshAsync()
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var snapshot = await _diagnostics.GetSnapshotAsync();
            RecordingStatusText.Text = snapshot.IsRecording ? "Recording" : "Idle";
            AutomaticStatusText.Text = snapshot.IsAutomaticCaptureEnabled ? "Enabled" : "Off";
            IndexedMediaText.Text = $"{snapshot.IndexedSegments} segment(s), {snapshot.DamagedSegments} damaged, {snapshot.MissingSegments} missing";
            RecordingStorageText.Text = FormatBytes(snapshot.RecordingBytes);
            RecoverySummaryText.Text = $"{snapshot.RecoverySummary}; {snapshot.PreservedRecoveryFiles} preserved backup file(s)";
            _allEntries = snapshot.LogEntries;
            ApplyFilter();
            OpenBackupsButton.IsEnabled = Directory.Exists(_diagnostics.RecoveryBackupDirectory);
            StatusText.Text = $"Showing {_visibleEntries.Count} recent event(s).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load Blackbox diagnostics.");
            StatusText.Text = $"Diagnostics failed: {ex.Message}";
        }
        finally
        {
            _isBusy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        if (CategoryComboBox.SelectedItem is not string selected)
        {
            return;
        }

        _visibleEntries.Clear();
        foreach (var entry in _allEntries.Where(entry =>
                     selected == "All" || entry.Category.ToString() == selected))
        {
            _visibleEntries.Add(new DiagnosticLogListItem(entry));
        }

        if (!_isBusy)
        {
            StatusText.Text = $"Showing {_visibleEntries.Count} recent event(s).";
        }
    }

    private void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open diagnostics directory {DirectoryPath}.", path);
            StatusText.Text = $"Could not open folder: {ex.Message}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}

internal sealed record DiagnosticLogListItem(DiagnosticLogEntry Entry)
{
    public string Timestamp => Entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string CategoryAndSeverity => $"{Entry.Category} / {Entry.Severity}";
    public string Message => Entry.Message;
}
