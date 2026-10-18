using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Blackbox.Domain;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

public partial class DiagnosticsPage : UserControl
{
    private readonly DiagnosticsService _diagnostics;
    private readonly ILogger<DiagnosticsPage> _logger;
    private readonly ObservableCollection<DiagnosticLogListItem> _visibleEntries = [];
    private IReadOnlyList<DiagnosticLogEntry> _allEntries = [];
    private bool _isBusy;
    private CancellationTokenSource? _testCancellation;
    private Task _testTask = Task.CompletedTask;
    internal Func<bool, int, CancellationToken, Task>? OpenRecordingSetupAsync { get; set; }

    public DiagnosticsPage(
        DiagnosticsService diagnostics,
        ILogger<DiagnosticsPage> logger)
    {
        _diagnostics = diagnostics;
        _logger = logger;
        InitializeComponent();
        LogListBox.ItemsSource = _visibleEntries;
        CategoryComboBox.ItemsSource = new[] { "All", "Recording", "Detection", "Export", "Recovery", "System" };
        CategoryComboBox.SelectedIndex = 0;
        PlaybackTestWarningText.Text = RecordingSetupProtocol.Warning;
        TestPatternComboBox.ItemsSource = new[]
        {
            new TestPatternOption(true, "Moving color pattern"),
            new TestPatternOption(false, "Still color bars")
        };
        TestPatternComboBox.SelectedIndex = 0;
        TestQualityComboBox.ItemsSource = new[]
        {
            new TestQualityOption(30, "720p window / 30 fps"),
            new TestQualityOption(60, "1080p window / 60 fps")
        };
        TestQualityComboBox.SelectedIndex = 0;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                ResetPlaybackTestConsent();
                DiagnosticsTabs.SelectedIndex = 0;
            }
        };
    }

    public Task ActivateAsync() => RefreshAsync();

    private void PlaybackTestConsent_Changed(object sender, RoutedEventArgs e) => UpdatePlaybackTestControls();

    private void DiagnosticsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != DiagnosticsTabs || PlaybackTestConsentCheckBox is null) return;
        if (DiagnosticsTabs.SelectedItem != SetupTab) ResetPlaybackTestConsent();
        UpdatePlaybackTestControls();
    }

    internal async Task StopPlaybackTestAsync()
    {
        _testCancellation?.Cancel();
        await _testTask;
    }

    private void ResetPlaybackTestConsent()
    {
        _testCancellation?.Cancel();
        PlaybackTestConsentCheckBox.IsChecked = false;
    }

    private void UpdatePlaybackTestControls()
    {
        if (PlayTestButton is null) return;
        var busy = _testCancellation is not null;
        PlayTestButton.IsEnabled = !busy && DiagnosticsTabs.SelectedItem == SetupTab && PlaybackTestConsentCheckBox.IsChecked == true;
        TestPatternComboBox.IsEnabled = !busy;
        TestQualityComboBox.IsEnabled = !busy;
        PlaybackTestConsentCheckBox.IsEnabled = !busy;
        CancelTestButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TestProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelTestButton_Click(object sender, RoutedEventArgs e) => _testCancellation?.Cancel();

    private async void PlayTestButton_Click(object sender, RoutedEventArgs e)
    {
        _testTask = PlayTestAsync();
        await _testTask;
    }

    private async Task PlayTestAsync()
    {
        if (_testCancellation is not null || DiagnosticsTabs.SelectedItem != SetupTab || PlaybackTestConsentCheckBox.IsChecked != true ||
            TestPatternComboBox.SelectedItem is not TestPatternOption pattern ||
            TestQualityComboBox.SelectedItem is not TestQualityOption quality || OpenRecordingSetupAsync is null)
            return;
        using var cancellation = new CancellationTokenSource();
        _testCancellation = cancellation;
        TestProgressBar.Value = 0;
        TestStatusText.Text = "Opening recording setup window...";
        UpdatePlaybackTestControls();
        try
        {
            await OpenRecordingSetupAsync(pattern.Value, quality.Value, cancellation.Token);
            TestStatusText.Text = "Setup window selected. Start the scene there, then press Record. Automatic capture resumes when it closes.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            TestStatusText.Text = "Recording setup canceled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open the recording setup window.");
            TestStatusText.Text = $"Recording setup failed: {ex.Message}";
        }
        finally
        {
            _testCancellation = null;
            PlaybackTestConsentCheckBox.IsChecked = false;
            UpdatePlaybackTestControls();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => OpenDirectory(_diagnostics.LogDirectory);

    private void OpenBackupsButton_Click(object sender, RoutedEventArgs e) => OpenDirectory(_diagnostics.RecoveryBackupDirectory);

    private async void ExportSupportBundleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            Window.GetWindow(this),
            _diagnostics.SupportBundlePrivacyDisclosure + "\n\nCreate this local support bundle?",
            "Support bundle privacy review",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Blackbox support bundle",
            Filter = "Zip archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"Blackbox-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        _isBusy = true;
        ExportSupportBundleButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Creating privacy-reviewed support bundle...";
        try
        {
            var result = await _diagnostics.ExportSupportBundleAsync(dialog.FileName);
            StatusText.Text =
                $"Support bundle saved with {result.IncludedLogEntries} event(s) and {result.RedactionCount} redaction(s).";
            MessageBox.Show(
                Window.GetWindow(this),
                $"Support bundle saved to:\n{result.FilePath}",
                "Support bundle ready",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create the Blackbox support bundle.");
            StatusText.Text = $"Support bundle failed: {ex.Message}";
        }
        finally
        {
            _isBusy = false;
            ExportSupportBundleButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
        }
    }

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
            GuardianSummaryText.Text = $"{snapshot.GuardianState}: {snapshot.GuardianSummary}";
            RecorderSummaryText.Text = snapshot.RecorderSummary;
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

internal sealed record TestPatternOption(bool Value, string Label)
{
    public override string ToString() => Label;
}

internal sealed record TestQualityOption(int Value, string Label)
{
    public override string ToString() => Label;
}

internal sealed record DiagnosticLogListItem(DiagnosticLogEntry Entry)
{
    public string Timestamp => Entry.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string FullTimestamp => Entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
    public string CategoryAndSeverity => $"{Entry.Category} / {(Entry.Severity == DiagnosticSeverity.Information ? "Info" : Entry.Severity.ToString())}";
    public string Message => Entry.Message;
}
