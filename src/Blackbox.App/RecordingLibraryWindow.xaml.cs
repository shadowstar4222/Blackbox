using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Blackbox.Domain;
using Blackbox.Export;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Blackbox.App;

public partial class RecordingLibraryWindow : Window
{
    private static readonly IReadOnlyList<ExportPresetOption> ExportPresets =
    [
        new(AudioExportPreset.FullMixOnly, "Full mix only"),
        new(AudioExportPreset.FullMixAndIsolated, "Full mix + isolated tracks"),
        new(AudioExportPreset.RawMicrophoneOnly, "Raw microphone only"),
        new(AudioExportPreset.ProcessedMicrophoneOnly, "Processed microphone only"),
        new(AudioExportPreset.MicrophonesRemoved, "Microphones removed"),
        new(AudioExportPreset.VoiceChatRemoved, "Voice chat removed"),
        new(AudioExportPreset.Custom, "Custom")
    ];

    private readonly RecordingLibraryService _libraryService;
    private readonly TimelineAssetService _timelineAssetService;
    private readonly SessionPlaybackService _playbackService;
    private readonly SessionExportService _exportService;
    private readonly RecordingSettings _recordingSettings;
    private readonly ILogger<RecordingLibraryWindow> _logger;
    private readonly CancellationTokenSource _windowCancellation = new();
    private CancellationTokenSource? _timelineAssetCancellation;
    private CancellationTokenSource? _exportCancellation;
    private IReadOnlyList<ThumbnailItem> _thumbnails = [];
    private IReadOnlyList<AudioTrackRow> _audioTrackRows = [];
    private string? _lastExportPath;
    private bool _isBusy;
    private bool _updatingSelection;
    private bool _updatingAudio;

    public RecordingLibraryWindow(
        RecordingLibraryService libraryService,
        TimelineAssetService timelineAssetService,
        SessionPlaybackService playbackService,
        SessionExportService exportService,
        RecordingSettings recordingSettings,
        ILogger<RecordingLibraryWindow> logger)
    {
        _libraryService = libraryService;
        _timelineAssetService = timelineAssetService;
        _playbackService = playbackService;
        _exportService = exportService;
        _recordingSettings = recordingSettings;
        _logger = logger;
        InitializeComponent();
        ExportPresetComboBox.ItemsSource = ExportPresets;
        WaveformTimeline.ScrubRequested += WaveformTimeline_ScrubRequested;
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshLibraryAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshLibraryAsync();

    private async Task RefreshLibraryAsync()
    {
        var selectedId = SelectedSession?.Id;
        await ExecuteAsync("Refresh recordings", async () =>
        {
            var progress = new Progress<RecordingLibraryProgress>(UpdateProgress);
            var sessions = await _libraryService.RefreshAsync(progress, _windowCancellation.Token);
            var items = sessions.Select(static session => new SessionListItem(session)).ToArray();
            SessionListBox.ItemsSource = items;
            SessionListBox.SelectedItem = selectedId is null
                ? items.FirstOrDefault()
                : items.FirstOrDefault(item => item.Session.Id == selectedId) ?? items.FirstOrDefault();
            StatusText.Text = items.Length == 0
                ? "No completed recordings found."
                : $"{items.Length} recording session(s) available.";
        });
    }

    private async void SessionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var session = SelectedSession;
        ConfigureSession(session);
        if (session is not null)
        {
            await LoadTimelineAssetsAsync(session);
        }
    }

    private void ConfigureSession(RecordingSession? session)
    {
        _timelineAssetCancellation?.Cancel();
        _thumbnails = [];
        ThumbnailItemsControl.ItemsSource = null;
        PreviewImage.Source = null;
        _updatingSelection = true;
        _updatingAudio = true;
        try
        {
            if (session is null)
            {
                SessionTitleText.Text = "Select a recording";
                SessionSummaryText.Text = "-";
                HealthText.Text = "-";
                HealthDetailText.Text = "-";
                PreviewStatusText.Text = "Timeline preview pending";
                SetTimelineMaximum(0);
                _audioTrackRows = [];
                AudioTrackItemsControl.ItemsSource = null;
                MarkerListBox.ItemsSource = null;
                return;
            }

            var first = session.Segments[0];
            SessionTitleText.Text = session.GameTitle == "Recording"
                ? $"Recording {session.StartTime.LocalDateTime:g}"
                : session.GameTitle;
            SessionSummaryText.Text =
                $"{session.StartTime.LocalDateTime:f} | {FormatDuration(session.Duration)} | " +
                $"{first.Width} x {first.Height} | {first.FrameRate:0.##} fps | {session.Segments.Count} segment(s)";
            SetHealth(session);
            SetTimelineMaximum(Math.Max(0.001, session.Duration.TotalSeconds));
            SelectionStartSlider.Value = 0;
            SelectionEndSlider.Value = SelectionEndSlider.Maximum;
            PlayheadSlider.Value = 0;
            _audioTrackRows = RecordingAudioLayout
                .CreateExportSelections(first.AudioTrackLayout)
                .Select(static track => new AudioTrackRow(track))
                .ToArray();
            AudioTrackItemsControl.ItemsSource = _audioTrackRows;
            ExportPresetComboBox.SelectedItem = ExportPresets[1];
            ApplyPreset(AudioExportPreset.FullMixAndIsolated);
            UpdateTimelineMetadata(session);
        }
        finally
        {
            _updatingAudio = false;
            _updatingSelection = false;
            UpdateSelectionText();
            UpdateCursor(TimeSpan.Zero);
            UpdateControlState();
        }
    }

    private async Task LoadTimelineAssetsAsync(RecordingSession session)
    {
        _timelineAssetCancellation?.Dispose();
        _timelineAssetCancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        var cancellation = _timelineAssetCancellation;
        if (session.HasMissingSegments || session.HasGaps || session.HasDamagedSegments)
        {
            PreviewStatusText.Text = "Preview unavailable for this session.";
            return;
        }

        try
        {
            PreviewStatusText.Text = "Preparing timeline preview...";
            var progress = new Progress<RecordingLibraryProgress>(update =>
                PreviewStatusText.Text = update.Message);
            var assets = await _timelineAssetService.GetOrCreateAsync(session, progress, cancellation.Token);
            if (SelectedSession?.Id != session.Id || cancellation.IsCancellationRequested)
            {
                return;
            }

            _thumbnails = assets.Thumbnails
                .Select(thumbnail => new ThumbnailItem(
                    thumbnail.Offset,
                    FormatDuration(thumbnail.Offset),
                    LoadImage(thumbnail.ImagePath)))
                .ToArray();
            ThumbnailItemsControl.ItemsSource = _thumbnails;
            WaveformTimeline.Samples = assets.Waveform;
            WaveformTimeline.Refresh();
            PreviewStatusText.Text = assets.LoadedFromCache
                ? "Timeline preview loaded."
                : "Timeline preview generated.";
            UpdatePreviewImage(TimeSpan.FromSeconds(PlayheadSlider.Value));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timeline preview generation failed for session {RecordingSessionId}.", session.Id);
            PreviewStatusText.Text = $"Timeline preview failed: {ex.Message}";
        }
    }

    private void SetHealth(RecordingSession session)
    {
        if (session.HasMissingSegments)
        {
            HealthText.Text = "Missing media";
            HealthText.Foreground = Brushes.Firebrick;
            HealthDetailText.Text = "One or more source segments were moved or deleted.";
        }
        else if (session.HasDamagedSegments)
        {
            var damaged = session.Segments.First(static segment => segment.IsDamaged);
            HealthText.Text = "Damaged media";
            HealthText.Foreground = Brushes.Firebrick;
            HealthDetailText.Text = $"{Path.GetFileName(damaged.FilePath)}: {damaged.DamageDetail}";
        }
        else if (session.HasGaps)
        {
            HealthText.Text = "Timeline gap";
            HealthText.Foreground = Brushes.DarkOrange;
            HealthDetailText.Text = "A discontinuity was detected between source segments.";
        }
        else
        {
            HealthText.Text = "Continuous and ready";
            HealthText.Foreground = Brushes.SeaGreen;
            HealthDetailText.Text = session.ProtectedRanges.Count == 0
                ? "No protected ranges"
                : $"{session.ProtectedRanges.Count} protected range(s)";
        }
    }

    private void SetTimelineMaximum(double maximum)
    {
        SelectionStartSlider.Maximum = maximum;
        SelectionEndSlider.Maximum = maximum;
        PlayheadSlider.Maximum = maximum;
        SelectionStartSlider.Value = 0;
        SelectionEndSlider.Value = maximum;
        PlayheadSlider.Value = 0;
    }

    private void UpdateTimelineMetadata(RecordingSession session)
    {
        WaveformTimeline.Duration = session.Duration;
        WaveformTimeline.SegmentBoundaries = RecordingTimeline.GetSegmentBoundaries(session);
        WaveformTimeline.ProtectedRanges = session.ProtectedRanges
            .Select(range => new TimelineDisplayRange(
                RecordingTimeline.ToOffset(session, range.StartTime),
                RecordingTimeline.ToOffset(session, range.EndTime)))
            .ToArray();
        var damagedRanges = new List<TimelineDisplayRange>();
        var elapsed = TimeSpan.Zero;
        foreach (var segment in session.Segments.OrderBy(static segment => segment.StartTime))
        {
            var duration = segment.EndTime - segment.StartTime;
            if (segment.IsDamaged)
            {
                damagedRanges.Add(new TimelineDisplayRange(elapsed, elapsed + duration));
            }

            elapsed += duration;
        }

        WaveformTimeline.DamagedRanges = damagedRanges;
        WaveformTimeline.Markers = session.Markers;
        WaveformTimeline.SelectionStart = TimeSpan.FromSeconds(SelectionStartSlider.Value);
        WaveformTimeline.SelectionEnd = TimeSpan.FromSeconds(SelectionEndSlider.Value);
        WaveformTimeline.CursorPosition = TimeSpan.FromSeconds(PlayheadSlider.Value);
        WaveformTimeline.Refresh();
        MarkerListBox.ItemsSource = session.Markers
            .Select(static marker => new MarkerItem(marker.Id, FormatDuration(marker.Offset), marker.Label))
            .ToArray();
    }

    private void SelectionStartSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingSelection && SelectionStartSlider.Value >= SelectionEndSlider.Value)
        {
            _updatingSelection = true;
            SelectionStartSlider.Value = Math.Max(0, SelectionEndSlider.Value - MinimumSelectionSeconds());
            _updatingSelection = false;
        }

        UpdateSelectionText();
    }

    private void SelectionEndSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingSelection && SelectionEndSlider.Value <= SelectionStartSlider.Value)
        {
            _updatingSelection = true;
            SelectionEndSlider.Value = Math.Min(
                SelectionEndSlider.Maximum,
                SelectionStartSlider.Value + MinimumSelectionSeconds());
            _updatingSelection = false;
        }

        UpdateSelectionText();
    }

    private void PlayheadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsInitialized)
        {
            UpdateCursor(TimeSpan.FromSeconds(PlayheadSlider.Value));
        }
    }

    private void WaveformTimeline_ScrubRequested(object? sender, TimeSpan offset)
    {
        PlayheadSlider.Value = offset.TotalSeconds;
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
            var start = TimeSpan.FromSeconds(Math.Min(
                PlayheadSlider.Value,
                Math.Max(0, session.Duration.TotalSeconds - 0.001)));
            await _playbackService.PlayAsync(
                session,
                start,
                new Progress<RecordingLibraryProgress>(UpdateProgress),
                _windowCancellation.Token);
            StatusText.Text = $"Continuous playback opened at {FormatDuration(start)}.";
        });
    }

    private async void AddMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Add marker", async () =>
        {
            var item = SelectedSessionItem
                ?? throw new InvalidOperationException("Select a recording first.");
            var marker = await _libraryService.AddMarkerAsync(
                item.Session,
                TimeSpan.FromSeconds(PlayheadSlider.Value),
                cancellationToken: _windowCancellation.Token);
            item.Session = item.Session with
            {
                Markers = item.Session.Markers.Append(marker).OrderBy(static value => value.Offset).ToArray()
            };
            UpdateTimelineMetadata(item.Session);
            StatusText.Text = $"Marker added at {FormatDuration(marker.Offset)}.";
        });
    }

    private async void DeleteMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: Guid markerId } || SelectedSessionItem is not { } item)
        {
            return;
        }

        await ExecuteAsync("Remove marker", async () =>
        {
            await _libraryService.DeleteMarkerAsync(markerId, _windowCancellation.Token);
            item.Session = item.Session with
            {
                Markers = item.Session.Markers.Where(marker => marker.Id != markerId).ToArray()
            };
            UpdateTimelineMetadata(item.Session);
            StatusText.Text = "Marker removed.";
        });
    }

    private async void ProtectSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Protect selection", async () =>
        {
            var item = SelectedSessionItem
                ?? throw new InvalidOperationException("Select a recording first.");
            var range = await _libraryService.ProtectRangeAsync(
                item.Session,
                TimeSpan.FromSeconds(SelectionStartSlider.Value),
                TimeSpan.FromSeconds(SelectionEndSlider.Value),
                _windowCancellation.Token);
            item.Session = item.Session with
            {
                ProtectedRanges = item.Session.ProtectedRanges.Append(range).ToArray()
            };
            SetHealth(item.Session);
            UpdateTimelineMetadata(item.Session);
            StatusText.Text = "Selection protected from quota deletion.";
        });
    }

    private void ExportPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAudio || ExportPresetComboBox.SelectedItem is not ExportPresetOption preset)
        {
            return;
        }

        ApplyPreset(preset.Value);
    }

    private void ApplyPreset(AudioExportPreset preset)
    {
        if (preset == AudioExportPreset.Custom)
        {
            return;
        }

        _updatingAudio = true;
        try
        {
            foreach (var track in _audioTrackRows)
            {
                track.IsSolo = false;
                track.Volume = 1;
                track.IsMuted = preset switch
                {
                    AudioExportPreset.FullMixOnly => track.StreamIndex != 0,
                    AudioExportPreset.RawMicrophoneOnly => !track.Name.Equals("Raw microphone", StringComparison.OrdinalIgnoreCase),
                    AudioExportPreset.ProcessedMicrophoneOnly => !track.Name.Equals("Processed microphone", StringComparison.OrdinalIgnoreCase),
                    AudioExportPreset.MicrophonesRemoved => track.Name.Contains("microphone", StringComparison.OrdinalIgnoreCase),
                    AudioExportPreset.VoiceChatRemoved => track.Name.Contains("voice", StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
            }
        }
        finally
        {
            _updatingAudio = false;
        }
    }

    private void AudioTrackControl_Changed(object sender, RoutedEventArgs e) => MarkPresetCustom();

    private void AudioVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider { DataContext: AudioTrackRow row })
        {
            row.Volume = e.NewValue;
        }

        MarkPresetCustom();
    }

    private void MarkPresetCustom()
    {
        if (_updatingAudio || !IsInitialized)
        {
            return;
        }

        _updatingAudio = true;
        ExportPresetComboBox.SelectedItem = ExportPresets[^1];
        _updatingAudio = false;
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
                destinationPath,
                _audioTrackRows.Select(static track => track.ToSelection()).ToArray());
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
            var wavCount = result.AudioOutputPaths?.Count ?? 0;
            StatusText.Text = wavCount == 0
                ? $"Export complete: {Path.GetFileName(result.OutputPath)}"
                : $"Export complete: one video and {wavCount} WAV file(s).";
        }
        catch (OperationCanceledException) when (_exportCancellation.IsCancellationRequested)
        {
            StatusText.Text = "Export canceled. No partial files were kept.";
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

    private void OpenExportButton_Click(object sender, RoutedEventArgs e) =>
        TryOpenPath(_lastExportPath, "Open export");

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

        var start = TimeSpan.FromSeconds(SelectionStartSlider.Value);
        var end = TimeSpan.FromSeconds(SelectionEndSlider.Value);
        SelectionStartText.Text = FormatDuration(start);
        SelectionEndText.Text = FormatDuration(end);
        ExportRangeText.Text = $"Selection {FormatDuration(start)} - {FormatDuration(end)}";
        WaveformTimeline.SelectionStart = start;
        WaveformTimeline.SelectionEnd = end;
        WaveformTimeline.Refresh();
    }

    private void UpdateCursor(TimeSpan offset)
    {
        CursorTimeText.Text = FormatDuration(offset);
        PlayheadTimeText.Text = FormatDuration(offset);
        WaveformTimeline.CursorPosition = offset;
        WaveformTimeline.Refresh();
        UpdatePreviewImage(offset);
    }

    private void UpdatePreviewImage(TimeSpan offset)
    {
        if (_thumbnails.Count == 0)
        {
            return;
        }

        PreviewImage.Source = _thumbnails
            .MinBy(thumbnail => Math.Abs((thumbnail.Offset - offset).Ticks))?
            .Image;
    }

    private void UpdateControlState()
    {
        var hasSession = SelectedSession is not null;
        var healthy = hasSession && SelectedSession is
        {
            HasGaps: false,
            HasMissingSegments: false,
            HasDamagedSegments: false
        };
        RefreshButton.IsEnabled = !_isBusy;
        SessionListBox.IsEnabled = !_isBusy;
        PlayButton.IsEnabled = !_isBusy && healthy;
        ExportButton.IsEnabled = !_isBusy && healthy;
        FullSessionButton.IsEnabled = !_isBusy && hasSession;
        AddMarkerButton.IsEnabled = !_isBusy && hasSession;
        ProtectSelectionButton.IsEnabled = !_isBusy && healthy;
        SelectionStartSlider.IsEnabled = !_isBusy && hasSession;
        SelectionEndSlider.IsEnabled = !_isBusy && hasSession;
        PlayheadSlider.IsEnabled = !_isBusy && healthy;
        ExportPresetComboBox.IsEnabled = !_isBusy && healthy;
        AudioTrackItemsControl.IsEnabled = !_isBusy && healthy;
    }

    private double MinimumSelectionSeconds() => Math.Min(1, SelectionEndSlider.Maximum);

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

    private SessionListItem? SelectedSessionItem => SessionListBox.SelectedItem as SessionListItem;
    private RecordingSession? SelectedSession => SelectedSessionItem?.Session;

    private static ImageSource LoadImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    private void Window_Closed(object? sender, EventArgs e)
    {
        _windowCancellation.Cancel();
        _timelineAssetCancellation?.Cancel();
        _exportCancellation?.Cancel();
        _timelineAssetCancellation?.Dispose();
        _exportCancellation?.Dispose();
        _windowCancellation.Dispose();
    }

    private sealed class SessionListItem(RecordingSession session)
    {
        public RecordingSession Session { get; set; } = session;
        public string Title => Session.GameTitle == "Recording" ? "Continuous recording" : Session.GameTitle;
        public string RecordedAt => Session.StartTime.LocalDateTime.ToString("g");
        public string Summary =>
            $"{FormatDuration(Session.Duration)} | {Session.Segments.Count} segment(s)" +
            (Session.HasGaps || Session.HasMissingSegments || Session.HasDamagedSegments ? " | Needs attention" : string.Empty);
    }

    private sealed record ThumbnailItem(TimeSpan Offset, string Time, ImageSource Image);
    private sealed record MarkerItem(Guid Id, string Time, string Label);
    private sealed record ExportPresetOption(AudioExportPreset Value, string Label)
    {
        public override string ToString() => Label;
    }

    private enum AudioExportPreset
    {
        FullMixOnly,
        FullMixAndIsolated,
        RawMicrophoneOnly,
        ProcessedMicrophoneOnly,
        MicrophonesRemoved,
        VoiceChatRemoved,
        Custom
    }

    private sealed class AudioTrackRow : INotifyPropertyChanged
    {
        private bool _isMuted;
        private bool _isSolo;
        private double _volume;
        private bool _exportAsWav;

        public AudioTrackRow(AudioTrackExportSelection selection)
        {
            StreamIndex = selection.StreamIndex;
            Name = selection.Name;
            _isMuted = selection.IsMuted;
            _isSolo = selection.IsSolo;
            _volume = selection.Volume;
            _exportAsWav = selection.ExportAsWav;
        }

        public int StreamIndex { get; }
        public string Name { get; }

        public bool IsMuted
        {
            get => _isMuted;
            set => SetField(ref _isMuted, value);
        }

        public bool IsSolo
        {
            get => _isSolo;
            set => SetField(ref _isSolo, value);
        }

        public double Volume
        {
            get => _volume;
            set
            {
                if (SetField(ref _volume, value))
                {
                    OnPropertyChanged(nameof(VolumeText));
                }
            }
        }

        public string VolumeText => $"{Volume * 100:0}%";

        public bool ExportAsWav
        {
            get => _exportAsWav;
            set => SetField(ref _exportAsWav, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public AudioTrackExportSelection ToSelection() => new(
            StreamIndex,
            Name,
            IsMuted,
            IsSolo,
            Volume,
            ExportAsWav);

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
