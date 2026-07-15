using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Blackbox.Domain;
using Blackbox.Export;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Blackbox.App;

public partial class RecordingLibraryWindow : Window
{
    private readonly RecordingLibraryService _libraryService;
    private readonly SessionPlaybackService _playbackService;
    private readonly SessionExportService _exportService;
    private readonly RecordingSettings _recordingSettings;
    private readonly ILogger<RecordingLibraryWindow> _logger;
    private readonly CancellationTokenSource _windowCancellation = new();
    private CancellationTokenSource? _exportCancellation;
    private string? _lastExportPath;
    private bool _isBusy;
    private bool _updatingSelection;

    public RecordingLibraryWindow(
        RecordingLibraryService libraryService,
        SessionPlaybackService playbackService,
        SessionExportService exportService,
        RecordingSettings recordingSettings,
        ILogger<RecordingLibraryWindow> logger)
    {
        _libraryService = libraryService;
        _playbackService = playbackService;
        _exportService = exportService;
        _recordingSettings = recordingSettings;
        _logger = logger;
        InitializeComponent();
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshLibraryAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshLibraryAsync();

    private async Task RefreshLibraryAsync()
    {
        await ExecuteAsync("Refresh recordings", async () =>
        {
            var progress = new Progress<RecordingLibraryProgress>(UpdateProgress);
            var sessions = await _libraryService.RefreshAsync(progress, _windowCancellation.Token);
            var items = sessions.Select(static session => new SessionListItem(session)).ToArray();
            SessionListBox.ItemsSource = items;
            SessionListBox.SelectedIndex = items.Length > 0 ? 0 : -1;
            StatusText.Text = items.Length == 0
                ? "No completed recordings found."
                : $"{items.Length} recording session(s) available.";
        });
    }

    private void SessionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var session = SelectedSession;
        _updatingSelection = true;
        try
        {
            if (session is null)
            {
                SessionTitleText.Text = "Select a recording";
                RecordedText.Text = "-";
                DurationText.Text = "-";
                MediaText.Text = "-";
                HealthText.Text = "-";
                SelectionStartSlider.Maximum = 0;
                SelectionEndSlider.Maximum = 0;
                SelectionStartSlider.Value = 0;
                SelectionEndSlider.Value = 0;
                return;
            }

            var first = session.Segments[0];
            SessionTitleText.Text = session.GameTitle == "Recording"
                ? $"Recording {session.StartTime.LocalDateTime:g}"
                : session.GameTitle;
            RecordedText.Text = session.StartTime.LocalDateTime.ToString("f");
            DurationText.Text = FormatDuration(session.Duration);
            MediaText.Text = $"{first.Width} x {first.Height} | {first.FrameRate:0.##} fps | {session.Segments.Count} segment(s)";
            HealthText.Text = session.HasMissingSegments
                ? "Missing source media"
                : session.HasGaps
                    ? "Timeline gap detected"
                    : "Continuous and ready";
            var durationSeconds = Math.Max(0.001, session.Duration.TotalSeconds);
            SelectionStartSlider.Maximum = durationSeconds;
            SelectionEndSlider.Maximum = durationSeconds;
            SelectionStartSlider.Value = 0;
            SelectionEndSlider.Value = durationSeconds;
        }
        finally
        {
            _updatingSelection = false;
            UpdateSelectionText();
            UpdateControlState();
        }
    }

    private void SelectionStartSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingSelection && SelectionStartSlider.Value >= SelectionEndSlider.Value)
        {
            _updatingSelection = true;
            SelectionStartSlider.Value = Math.Max(0, SelectionEndSlider.Value - 1);
            _updatingSelection = false;
        }

        UpdateSelectionText();
    }

    private void SelectionEndSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingSelection && SelectionEndSlider.Value <= SelectionStartSlider.Value)
        {
            _updatingSelection = true;
            SelectionEndSlider.Value = Math.Min(SelectionEndSlider.Maximum, SelectionStartSlider.Value + 1);
            _updatingSelection = false;
        }

        UpdateSelectionText();
    }

    private void FullSessionButton_Click(object sender, RoutedEventArgs e)
    {
        _updatingSelection = true;
        SelectionStartSlider.Value = 0;
        SelectionEndSlider.Value = SelectionEndSlider.Maximum;
        _updatingSelection = false;
        UpdateSelectionText();
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Play recording", async () =>
        {
            var session = SelectedSession
                ?? throw new InvalidOperationException("Select a recording first.");
            await _playbackService.PlayAsync(
                session,
                new Progress<RecordingLibraryProgress>(UpdateProgress),
                _windowCancellation.Token);
            StatusText.Text = "Continuous playback opened.";
        });
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession;
        if (session is null)
        {
            return;
        }

        var exportDirectory = Path.Combine(_recordingSettings.RecordingLocation, "Exports");
        Directory.CreateDirectory(exportDirectory);
        var dialog = new SaveFileDialog
        {
            Title = "Export Blackbox recording",
            InitialDirectory = exportDirectory,
            FileName = $"Blackbox {session.StartTime.LocalDateTime:yyyy-MM-dd HH-mm-ss}.mkv",
            Filter = "Matroska video (*.mkv)|*.mkv|MP4 video (*.mp4)|*.mp4",
            DefaultExt = ".mkv",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var selectedExtension = dialog.FilterIndex == 2 ? ".mp4" : ".mkv";
        var destinationPath = Path.GetExtension(dialog.FileName).Equals(
            selectedExtension,
            StringComparison.OrdinalIgnoreCase)
            ? dialog.FileName
            : Path.ChangeExtension(dialog.FileName, selectedExtension);

        _exportCancellation?.Dispose();
        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        _isBusy = true;
        CancelExportButton.Visibility = Visibility.Visible;
        OpenExportButton.Visibility = Visibility.Collapsed;
        OperationProgressBar.Visibility = Visibility.Visible;
        UpdateControlState();
        try
        {
            var request = new SessionExportRequest(
                session,
                TimeSpan.FromSeconds(SelectionStartSlider.Value),
                TimeSpan.FromSeconds(SelectionEndSlider.Value),
                destinationPath);
            var progress = new Progress<ExportProgress>(update =>
            {
                StatusText.Text = update.Message;
                OperationProgressBar.IsIndeterminate = update.Percent is null;
                if (update.Percent is not null)
                {
                    OperationProgressBar.Value = update.Percent.Value;
                }
            });
            var result = await _exportService.ExportAsync(request, progress, _exportCancellation.Token);
            _lastExportPath = result.OutputPath;
            OpenExportButton.Visibility = Visibility.Visible;
            StatusText.Text = $"Export complete: {Path.GetFileName(result.OutputPath)}";
        }
        catch (OperationCanceledException) when (_exportCancellation.IsCancellationRequested)
        {
            StatusText.Text = "Export canceled. No partial video was kept.";
        }
        catch (Exception ex)
        {
            ReportFailure("Export recording", ex);
        }
        finally
        {
            _isBusy = false;
            CancelExportButton.Visibility = Visibility.Collapsed;
            OperationProgressBar.Visibility = Visibility.Collapsed;
            UpdateControlState();
        }
    }

    private void CancelExportButton_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    private void OpenExportButton_Click(object sender, RoutedEventArgs e) => TryOpenPath(_lastExportPath, "Open export");

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_recordingSettings.RecordingLocation);
        TryOpenPath(_recordingSettings.RecordingLocation, "Open recordings folder");
    }

    private async Task ExecuteAsync(string commandName, Func<Task> command)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        OperationProgressBar.Visibility = Visibility.Visible;
        UpdateControlState();
        try
        {
            await command();
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportFailure(commandName, ex);
        }
        finally
        {
            _isBusy = false;
            OperationProgressBar.Visibility = Visibility.Collapsed;
            UpdateControlState();
        }
    }

    private void UpdateProgress(RecordingLibraryProgress update)
    {
        StatusText.Text = update.Message;
        OperationProgressBar.IsIndeterminate = update.Percent is null;
        if (update.Percent is not null)
        {
            OperationProgressBar.Value = update.Percent.Value;
        }
    }

    private void UpdateSelectionText()
    {
        if (!IsInitialized)
        {
            return;
        }

        SelectionStartText.Text = FormatDuration(TimeSpan.FromSeconds(SelectionStartSlider.Value));
        SelectionEndText.Text = FormatDuration(TimeSpan.FromSeconds(SelectionEndSlider.Value));
    }

    private void UpdateControlState()
    {
        var hasSession = SelectedSession is not null;
        RefreshButton.IsEnabled = !_isBusy;
        SessionListBox.IsEnabled = !_isBusy;
        PlayButton.IsEnabled = !_isBusy && hasSession;
        ExportButton.IsEnabled = !_isBusy && hasSession;
        FullSessionButton.IsEnabled = !_isBusy && hasSession;
        SelectionStartSlider.IsEnabled = !_isBusy && hasSession;
        SelectionEndSlider.IsEnabled = !_isBusy && hasSession;
    }

    private void TryOpenPath(string? path, string commandName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                throw new InvalidOperationException("The requested file or folder could not be found.");
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ReportFailure(commandName, ex);
        }
    }

    private void ReportFailure(string commandName, Exception exception)
    {
        _logger.LogError(exception, "{CommandName} failed.", commandName);
        StatusText.Text = $"{commandName} failed: {exception.Message}";
    }

    private RecordingSession? SelectedSession =>
        (SessionListBox.SelectedItem as SessionListItem)?.Session;

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    private void Window_Closed(object? sender, EventArgs e)
    {
        _windowCancellation.Cancel();
        _exportCancellation?.Cancel();
        _exportCancellation?.Dispose();
        _windowCancellation.Dispose();
    }

    private sealed class SessionListItem(RecordingSession session)
    {
        public RecordingSession Session { get; } = session;
        public string Title { get; } = session.GameTitle == "Recording"
            ? "Continuous recording"
            : session.GameTitle;
        public string RecordedAt { get; } = session.StartTime.LocalDateTime.ToString("g");
        public string Summary { get; } =
            $"{FormatDuration(session.Duration)} | {session.Segments.Count} segment(s)" +
            (session.HasGaps || session.HasMissingSegments ? " | Needs attention" : string.Empty);
    }
}
