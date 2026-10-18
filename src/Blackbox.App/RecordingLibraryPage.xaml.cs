using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Blackbox.Domain;
using Blackbox.Export;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Blackbox.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The page exposes deterministic disposal to its owning main window.")]
public partial class RecordingLibraryPage : UserControl, IDisposable
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
    private static readonly IReadOnlyList<NoteTargetOption> NoteTargets =
    [
        new(RecordingNoteTarget.Session, "Complete session"),
        new(RecordingNoteTarget.Marker, "Selected marker"),
        new(RecordingNoteTarget.TimelineRange, "Current selection")
    ];

    private readonly RecordingLibraryService _libraryService;
    private readonly TimelineAssetService _timelineAssetService;
    private readonly SessionExportService _exportService;
    private readonly InstantClipService _instantClipService;
    private readonly RecordingNoteService _noteService;
    private readonly ISegmentUsageRegistry _segmentUsageRegistry;
    private readonly RecordingSettings _recordingSettings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RecordingLibraryPage> _logger;
    private readonly CancellationTokenSource _pageCancellation = new();
    private CancellationTokenSource? _timelineAssetCancellation;
    private CancellationTokenSource? _exportCancellation;
    private IReadOnlyList<ThumbnailItem> _thumbnails = [];
    private IReadOnlyList<AudioTrackRow> _audioTrackRows = [];
    private PlaybackPage? _playbackPage;
    private readonly PlaybackRuntime _playbackRuntime;
    private readonly IMediaProbe _mediaProbe;
    private readonly RecordingCoordinator _coordinator;
    private readonly IObsController _recorder;
    private readonly DispatcherTimer _followTimer;
    private bool _openingPlayback;
    private bool _openingMediaFile;
    private Task _playbackRelease = Task.CompletedTask;
    private bool _refreshingFollow;
    private FileSystemWatcher? _followWatcher;
    private long _followDirtyAt;
    private bool _followRequested;
    private DateTimeOffset _followOrigin;
    private RecordingNote? _selectedClipNote;
    private RecordingNote? _selectedNote;
    private string? _lastExportPath;
    private bool _isBusy;
    private bool _isRefreshingClips;
    private bool _updatingSelection;
    private bool _updatingAudio;
    private bool _disposed;
    private ICollectionView? _sessionView;

    public RecordingLibraryPage(
        RecordingLibraryService libraryService,
        TimelineAssetService timelineAssetService,
        SessionExportService exportService,
        InstantClipService instantClipService,
        RecordingNoteService noteService,
        ISegmentUsageRegistry segmentUsageRegistry,
        RecordingSettings recordingSettings,
        ILoggerFactory loggerFactory,
        ILogger<RecordingLibraryPage> logger,
        PlaybackRuntime playbackRuntime,
        IMediaProbe mediaProbe,
        RecordingCoordinator coordinator,
        IObsController recorder)
    {
        _libraryService = libraryService;
        _timelineAssetService = timelineAssetService;
        _exportService = exportService;
        _instantClipService = instantClipService;
        _noteService = noteService;
        _segmentUsageRegistry = segmentUsageRegistry;
        _recordingSettings = recordingSettings;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _playbackRuntime = playbackRuntime;
        _mediaProbe = mediaProbe;
        _coordinator = coordinator;
        _recorder = recorder;
        _followTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _followTimer.Tick += FollowTimer_Tick;
        _coordinator.RecordingStateChanged += FollowRecordingStateChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && _playbackPage is not null) { _ = ClosePlaybackSafelyAsync(); return; }
            if (!_followRequested) return;
            if (_followWatcher is not null) _followWatcher.EnableRaisingEvents = IsVisible;
            if (IsVisible) { Interlocked.Exchange(ref _followDirtyAt, Environment.TickCount64); _followTimer.Start(); }
            else _followTimer.Stop();
        };
        InitializeComponent();
        ExportPresetComboBox.ItemsSource = ExportPresets;
        NoteTargetComboBox.ItemsSource = NoteTargets;
        NoteTargetComboBox.SelectedIndex = 0;
        WaveformTimeline.ScrubRequested += WaveformTimeline_ScrubRequested;
        _instantClipService.ClipsChanged += InstantClipService_ClipsChanged;
    }

    public async Task ActivateAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await RefreshLibraryAsync();
        await RefreshClipInboxAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshLibraryAsync();

    private async void RefreshClipsButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshClipInboxAsync();

    private async Task RefreshClipInboxAsync()
    {
        if (_disposed || _isRefreshingClips)
        {
            return;
        }

        _isRefreshingClips = true;
        RefreshClipsButton.IsEnabled = false;
        try
        {
            var selectedId = SelectedClip?.Id;
            var clips = await _instantClipService.GetAllAsync(_pageCancellation.Token);
            var items = clips.Select(static clip => new ClipListItem(clip)).ToArray();
            ClipListBox.ItemsSource = items;
            ClipListBox.SelectedItem = selectedId is null
                ? items.FirstOrDefault()
                : items.FirstOrDefault(item => item.Clip.Id == selectedId) ?? items.FirstOrDefault();
            var pending = items.Count(static item =>
                item.Clip.Status == InstantClipStatus.Pending);
            var failed = items.Count(static item =>
                item.Clip.Status == InstantClipStatus.Failed);
            ClipInboxSummaryText.Text = items.Length == 0
                ? "No clips"
                : $"{items.Length} clip(s) | {pending} pending | {failed} failed";
            await LoadSelectedClipNoteAsync();
        }
        catch (OperationCanceledException) when (_pageCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportFailure("Refresh clip inbox", ex);
        }
        finally
        {
            _isRefreshingClips = false;
            UpdateClipControls();
        }
    }

    private async void ClipListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadSelectedClipNoteAsync();
        UpdateClipControls();
    }

    private async Task LoadSelectedClipNoteAsync()
    {
        _selectedClipNote = null;
        ClipNoteTextBox.Text = string.Empty;
        if (SelectedClip is null)
        {
            UpdateClipControls();
            return;
        }

        var notes = await _noteService.GetAsync(
            RecordingNoteTarget.InstantClip,
            SelectedClip.Id,
            _pageCancellation.Token);
        _selectedClipNote = notes.FirstOrDefault();
        ClipNoteTextBox.Text = _selectedClipNote?.Text ?? string.Empty;
        UpdateClipControls();
    }

    private async void OpenClipButton_Click(object sender, RoutedEventArgs e) =>
        await OpenMediaFileAsync(SelectedClip?.OutputPath, "Open clip");

    private async void OpenClipSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var clip = SelectedClip;
        if (clip?.SessionId is null)
        {
            return;
        }

        var source = SessionListBox.Items
            .OfType<SessionListItem>()
            .FirstOrDefault(item => item.Session.Id == clip.SessionId.Value);
        if (source is null)
        {
            await RefreshLibraryAsync();
            source = SessionListBox.Items
                .OfType<SessionListItem>()
                .FirstOrDefault(item => item.Session.Id == clip.SessionId.Value);
        }

        if (source is null)
        {
            StatusText.Text = "The source session is not currently available.";
            return;
        }

        SessionListBox.SelectedItem = source;
        RecordingTabs.SelectedItem = TimelineTab;
        if (clip.ActualStartTime is not null)
        {
            var offset = RecordingTimeline.ToOffset(source.Session, clip.ActualStartTime.Value);
            PlayheadSlider.Value = offset.TotalSeconds;
            SelectionStartSlider.Value = offset.TotalSeconds;
            SelectionEndSlider.Value = RecordingTimeline
                .ToOffset(
                    source.Session,
                    clip.ActualEndTime ?? clip.ActualStartTime.Value)
                .TotalSeconds;
        }

        StatusText.Text = $"Opened source for {clip.Label}.";
    }

    private async void ProtectClipButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Update clip protection", async () =>
        {
            var clip = SelectedClip ??
                throw new InvalidOperationException("Select an instant clip.");
            await _instantClipService.SetProtectedAsync(
                clip.Id,
                clip.Status != InstantClipStatus.Protected,
                _pageCancellation.Token);
            await RefreshClipInboxAsync();
        });
    }

    private async void RetryClipButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Retry clip", async () =>
        {
            var clip = SelectedClip ??
                throw new InvalidOperationException("Select a failed clip.");
            await _instantClipService.RetryAsync(clip.Id, _pageCancellation.Token);
            await RefreshClipInboxAsync();
        });
    }

    private async void ExportedClipButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Mark clip exported", async () =>
        {
            var clip = SelectedClip ??
                throw new InvalidOperationException("Select a finished clip.");
            await _instantClipService.MarkExportedAsync(clip.Id, _pageCancellation.Token);
            await RefreshClipInboxAsync();
        });
    }

    private async void DiscardClipButton_Click(object sender, RoutedEventArgs e)
    {
        var clip = SelectedClip;
        if (clip is null)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            Window.GetWindow(this),
            clip.IsPlayable
                ? $"Discard {clip.Label} and delete its clip file?"
                : $"Discard {clip.Label} and release its source footage?",
            "Discard instant clip",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteAsync("Discard clip", async () =>
        {
            await _instantClipService.DiscardAsync(clip.Id, _pageCancellation.Token);
            await RefreshClipInboxAsync();
        });
    }

    private async void SaveClipNoteButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Save clip note", async () =>
        {
            var clip = SelectedClip ??
                throw new InvalidOperationException("Select an instant clip.");
            if (string.IsNullOrWhiteSpace(ClipNoteTextBox.Text))
            {
                throw new InvalidOperationException("Enter a note before saving.");
            }

            _selectedClipNote = await _noteService.SaveAsync(
                RecordingNoteTarget.InstantClip,
                clip.Id,
                ClipNoteTextBox.Text,
                _selectedClipNote?.Id,
                cancellationToken: _pageCancellation.Token);
            StatusText.Text = "Clip note saved.";
            UpdateClipControls();
        });
    }

    private async void DeleteClipNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClipNote is null)
        {
            return;
        }

        await ExecuteAsync("Delete clip note", async () =>
        {
            await _noteService.DeleteAsync(_selectedClipNote.Id, _pageCancellation.Token);
            _selectedClipNote = null;
            ClipNoteTextBox.Clear();
            StatusText.Text = "Clip note deleted.";
            UpdateClipControls();
        });
    }

    private void InstantClipService_ClipsChanged()
    {
        _ = Dispatcher.InvokeAsync(RefreshClipInboxAsync);
    }

    private async Task RefreshLibraryAsync()
    {
        var selectedId = SelectedSession?.Id;
        await ExecuteAsync("Refresh recordings", async () =>
        {
            var progress = new Progress<RecordingLibraryProgress>(UpdateProgress);
            var sessions = await Task.Run(() => _libraryService.RefreshAsync(progress, _pageCancellation.Token), _pageCancellation.Token);
            var items = sessions.Select(static session => new SessionListItem(session)).ToArray();
            _sessionView = CollectionViewSource.GetDefaultView(items);
            SessionListBox.ItemsSource = _sessionView;
            ApplySessionFilters();
            var visible = _sessionView.Cast<SessionListItem>();
            SessionListBox.SelectedItem = visible.FirstOrDefault(item => item.Session.Id == selectedId) ?? visible.FirstOrDefault();
        });
    }

    private void SessionFilters_Changed(object sender, RoutedEventArgs e) => ApplySessionFilters();

    private void ApplySessionFilters()
    {
        if (_sessionView is null) return;
        var terms = SessionSearchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var filter = SessionFilterComboBox.SelectedIndex;
        using (_sessionView.DeferRefresh())
        {
            _sessionView.Filter = value =>
            {
                if (value is not SessionListItem item) return false;
                var session = item.Session;
                if (filter == 1 && session.StartTime.LocalDateTime.Date != DateTime.Today) return false;
                if (filter == 2 && session.ProtectedRanges.Count == 0 && !session.Segments.Any(static segment => segment.IsProtected)) return false;
                if (filter == 3 && !session.HasGaps && !session.HasMissingSegments && !session.HasDamagedSegments) return false;
                return terms.All(term =>
                    item.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    item.RecordedAt.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    session.StartTime.LocalDateTime.ToString("yyyy-MM-dd").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    session.Markers.Any(marker => marker.Label.Contains(term, StringComparison.OrdinalIgnoreCase)));
            };
            _sessionView.SortDescriptions.Clear();
            _sessionView.SortDescriptions.Add(SessionSortComboBox.SelectedIndex switch
            {
                1 => new SortDescription(nameof(SessionListItem.SortTime), ListSortDirection.Ascending),
                2 => new SortDescription(nameof(SessionListItem.SortDuration), ListSortDirection.Descending),
                _ => new SortDescription(nameof(SessionListItem.SortTime), ListSortDirection.Descending)
            });
        }

        if (SessionListBox.SelectedItem is null) SessionListBox.SelectedItem = _sessionView.Cast<SessionListItem>().FirstOrDefault();
        var visible = _sessionView.Cast<SessionListItem>().Count();
        StatusText.Text = visible == 0 ? "No matching recordings." : $"{visible} recording{(visible == 1 ? string.Empty : "s")}";
    }

    private async void SessionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var session = SelectedSession;
        ConfigureSession(session);
        if (session is not null)
        {
            await LoadTimelineAssetsAsync(session, generateIfMissing: false);
        }

        await RefreshNotesAsync();
    }

    private void ConfigureSession(RecordingSession? session)
    {
        TimelineDetailsPanel.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        LibraryEmptyState.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        _timelineAssetCancellation?.Cancel();
        _thumbnails = [];
        ThumbnailItemsControl.ItemsSource = null;
        PreviewImage.Source = null;
        WaveformTimeline.Samples = [];
        BuildPreviewButton.Visibility = Visibility.Collapsed;
        BuildPreviewButton.IsEnabled = true;
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
                NoteMarkerComboBox.ItemsSource = null;
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
            var availablePresets = ExportPresets.Where(preset => preset.Value switch
            {
                AudioExportPreset.RawMicrophoneOnly => _audioTrackRows.Any(static track =>
                    track.Name.Equals("Raw microphone", StringComparison.OrdinalIgnoreCase)),
                AudioExportPreset.ProcessedMicrophoneOnly => _audioTrackRows.Any(static track =>
                    track.Name.Equals("Processed microphone", StringComparison.OrdinalIgnoreCase)),
                AudioExportPreset.MicrophonesRemoved => _audioTrackRows.Any(static track =>
                    track.Name.Contains("microphone", StringComparison.OrdinalIgnoreCase)),
                AudioExportPreset.VoiceChatRemoved => _audioTrackRows.Any(static track =>
                    track.Name.Contains("voice", StringComparison.OrdinalIgnoreCase)),
                AudioExportPreset.FullMixAndIsolated => _audioTrackRows.Count > 1,
                _ => true
            }).ToArray();
            ExportPresetComboBox.ItemsSource = availablePresets;
            var defaultPreset = _audioTrackRows.Count > 1 ? AudioExportPreset.FullMixAndIsolated : AudioExportPreset.FullMixOnly;
            ExportPresetComboBox.SelectedItem = availablePresets.First(preset => preset.Value == defaultPreset);
            ApplyPreset(defaultPreset);
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

    private async Task LoadTimelineAssetsAsync(RecordingSession session, bool generateIfMissing)
    {
        _timelineAssetCancellation?.Cancel();
        _timelineAssetCancellation?.Dispose();
        _timelineAssetCancellation = CancellationTokenSource.CreateLinkedTokenSource(_pageCancellation.Token);
        var cancellation = _timelineAssetCancellation;
        if (session.HasMissingSegments || session.HasGaps || session.HasDamagedSegments)
        {
            PreviewStatusText.Text = "Preview unavailable for this session.";
            BuildPreviewButton.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            BuildPreviewButton.IsEnabled = false;
            PreviewStatusText.Text = generateIfMissing
                ? "Preparing timeline preview..."
                : "Loading timeline preview...";
            var progress = new Progress<RecordingLibraryProgress>(update =>
                PreviewStatusText.Text = update.Message);
            var assets = generateIfMissing
                ? await _timelineAssetService.GetOrCreateAsync(session, progress, cancellation.Token)
                : await _timelineAssetService.TryGetCachedAsync(session, cancellation.Token);
            if (SelectedSession?.Id != session.Id || cancellation.IsCancellationRequested)
            {
                return;
            }

            if (assets is null)
            {
                PreviewStatusText.Text = "Visual preview has not been built.";
                BuildPreviewButton.Visibility = Visibility.Visible;
                return;
            }

            _thumbnails = await Task.Run(
                () => assets.Thumbnails
                    .Select(thumbnail => new ThumbnailItem(
                        thumbnail.Offset,
                        FormatDuration(thumbnail.Offset),
                        LoadImage(thumbnail.ImagePath)))
                    .ToArray(),
                cancellation.Token);
            if (SelectedSession?.Id != session.Id || cancellation.IsCancellationRequested)
            {
                return;
            }

            ThumbnailItemsControl.ItemsSource = _thumbnails;
            WaveformTimeline.Samples = assets.Waveform;
            WaveformTimeline.Refresh();
            BuildPreviewButton.Visibility = Visibility.Collapsed;
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
            if (cancellation.IsCancellationRequested || SelectedSession?.Id != session.Id)
            {
                return;
            }

            _logger.LogWarning(ex, "Timeline preview generation failed for session {RecordingSessionId}.", session.Id);
            PreviewStatusText.Text = $"Timeline preview failed: {ex.Message}";
            BuildPreviewButton.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_timelineAssetCancellation, cancellation))
            {
                BuildPreviewButton.IsEnabled = true;
            }
        }
    }

    private async void BuildPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is { } session)
        {
            await LoadTimelineAssetsAsync(session, generateIfMissing: true);
        }
    }

    private void SetHealth(RecordingSession session)
    {
        if (session.HasMissingSegments)
        {
            HealthText.Text = "Missing media";
            HealthText.Foreground = (Brush)FindResource("RecordingBrush");
            HealthDetailText.Text = "One or more source segments were moved or deleted.";
        }
        else if (session.HasDamagedSegments)
        {
            var damaged = session.Segments.First(static segment => segment.IsDamaged);
            HealthText.Text = "Damaged media";
            HealthText.Foreground = (Brush)FindResource("RecordingBrush");
            HealthDetailText.Text = $"{Path.GetFileName(damaged.FilePath)}: {damaged.DamageDetail}";
        }
        else if (session.HasGaps)
        {
            HealthText.Text = "Timeline gap";
            HealthText.Foreground = (Brush)FindResource("WarningBrush");
            HealthDetailText.Text = "A discontinuity was detected between source segments.";
        }
        else
        {
            HealthText.Text = "Continuous and ready";
            HealthText.Foreground = (Brush)FindResource("SuccessBrush");
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
            .Select(static marker => new MarkerItem(
                marker.Id,
                FormatDuration(marker.Offset),
                marker.Label,
                $"{marker.Origin} | -{FormatCompactDuration(marker.ContextBefore)} / +{FormatCompactDuration(marker.ContextAfter)}"))
            .ToArray();
        NoteMarkerComboBox.ItemsSource = MarkerListBox.ItemsSource;
        NoteMarkerComboBox.SelectedIndex =
            NoteMarkerComboBox.Items.Count == 0 ? -1 : 0;
    }

    private async void NoteTargetComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        NoteMarkerComboBox.IsEnabled =
            SelectedNoteTarget?.Value == RecordingNoteTarget.Marker;
        await RefreshNotesAsync();
    }

    private async void NoteMarkerComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        await RefreshNotesAsync();

    private async void MarkerListBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (MarkerListBox.SelectedItem is MarkerItem marker)
        {
            NoteMarkerComboBox.SelectedItem = NoteMarkerComboBox.Items
                .OfType<MarkerItem>()
                .FirstOrDefault(item => item.Id == marker.Id);
        }

        if (SelectedNoteTarget?.Value == RecordingNoteTarget.Marker)
        {
            await RefreshNotesAsync();
        }
    }

    private async Task RefreshNotesAsync()
    {
        _selectedNote = null;
        NoteTextBox.Text = string.Empty;
        if (!TryGetNoteTarget(out var target, out var targetId, out _, out _))
        {
            NoteListBox.ItemsSource = null;
            UpdateNoteControls();
            return;
        }

        try
        {
            var notes = await _noteService.GetAsync(
                target,
                targetId,
                _pageCancellation.Token);
            NoteListBox.ItemsSource = notes
                .Select(static note => new NoteListItem(note))
                .ToArray();
        }
        catch (OperationCanceledException) when (_pageCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportFailure("Load recording notes", ex);
        }

        UpdateNoteControls();
    }

    private void NoteListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedNote = (NoteListBox.SelectedItem as NoteListItem)?.Note;
        NoteTextBox.Text = _selectedNote?.Text ?? string.Empty;
        UpdateNoteControls();
    }

    private async void SaveNoteButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Save recording note", async () =>
        {
            if (!TryGetNoteTarget(
                    out var target,
                    out var targetId,
                    out var rangeStart,
                    out var rangeEnd))
            {
                throw new InvalidOperationException("Select a recording note target.");
            }

            if (string.IsNullOrWhiteSpace(NoteTextBox.Text))
            {
                throw new InvalidOperationException("Enter a note before saving.");
            }

            _selectedNote = await _noteService.SaveAsync(
                target,
                targetId,
                NoteTextBox.Text,
                _selectedNote?.Id,
                rangeStart,
                rangeEnd,
                _pageCancellation.Token);
            await RefreshNotesAsync();
            StatusText.Text = "Recording note saved.";
        });
    }

    private void NewNoteButton_Click(object sender, RoutedEventArgs e)
    {
        NoteListBox.SelectedItem = null;
        _selectedNote = null;
        NoteTextBox.Clear();
        NoteTextBox.Focus();
        UpdateNoteControls();
    }

    private async void DeleteNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNote is null)
        {
            return;
        }

        await ExecuteAsync("Delete recording note", async () =>
        {
            await _noteService.DeleteAsync(_selectedNote.Id, _pageCancellation.Token);
            _selectedNote = null;
            await RefreshNotesAsync();
            StatusText.Text = "Recording note deleted.";
        });
    }

    private bool TryGetNoteTarget(
        out RecordingNoteTarget target,
        out Guid targetId,
        out TimeSpan? rangeStart,
        out TimeSpan? rangeEnd)
    {
        target = SelectedNoteTarget?.Value ?? RecordingNoteTarget.Session;
        targetId = Guid.Empty;
        rangeStart = null;
        rangeEnd = null;
        var session = SelectedSession;
        if (session is null)
        {
            return false;
        }

        if (target == RecordingNoteTarget.Marker)
        {
            if (NoteMarkerComboBox.SelectedItem is not MarkerItem marker)
            {
                return false;
            }

            targetId = marker.Id;
            return true;
        }

        targetId = session.Id;
        if (target == RecordingNoteTarget.TimelineRange)
        {
            rangeStart = TimeSpan.FromSeconds(SelectionStartSlider.Value);
            rangeEnd = TimeSpan.FromSeconds(SelectionEndSlider.Value);
            if (rangeEnd <= rangeStart)
            {
                return false;
            }
        }

        return true;
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
        try
        {
            var item = SelectedSessionItem
                ?? throw new InvalidOperationException("Select a recording first.");
            var session = item.Session;
            var start = TimeSpan.FromSeconds(Math.Min(
                PlayheadSlider.Value,
                Math.Max(0, session.Duration.TotalSeconds - 0.001)));
            if (_playbackPage is { } existing && existing.Session.Id == session.Id)
            {
                existing.ShowAt(start);
                StatusText.Text = $"Review moved to {FormatDuration(start)}.";
                return;
            }

            await OpenPlaybackAsync(session, start);
        }
        catch (Exception ex)
        {
            ReportFailure("Play recording", ex);
        }
    }

    private async Task OpenPlaybackAsync(RecordingSession session, TimeSpan start, bool allowMarkers = true,
        bool follow = false)
    {
        if (_openingPlayback || _disposed) return;
        _openingPlayback = true;
        PlayButton.IsEnabled = false;
        try
        {
            await ClosePlaybackAsync(stopFollowing: !follow);
            var runtime = await _playbackRuntime.GetAsync();
            if (_disposed || !IsVisible || follow && !_followRequested) return;
            var player = new PlaybackPage(session, start, _libraryService, _segmentUsageRegistry,
                _loggerFactory.CreateLogger<PlaybackPage>(), _playbackRuntime, runtime, allowMarkers,
                startMuted: _coordinator.IsRecording || follow)
                { FollowRecording = follow };
            _playbackPage = player;
            try { await player.PrepareContinuousInputAsync(); }
            catch
            {
                await ClosePlaybackAsync();
                throw;
            }
            if (_disposed || !IsVisible || _playbackPage != player)
            {
                if (_playbackPage == player) await ClosePlaybackAsync();
                return;
            }
            player.MarkersChanged += (_, _) =>
            {
                if (SelectedSessionItem is { } item && item.Session.Id == player.Session.Id)
                {
                    item.Session = item.Session with { Markers = player.Session.Markers };
                    UpdateTimelineMetadata(item.Session);
                }
            };
            _timelineAssetCancellation?.Cancel();
            PlaybackHost.Content = player;
            LibrarySurface.Visibility = Visibility.Collapsed;
            PlaybackHost.Visibility = Visibility.Visible;
            player.Focus();
        }
        finally { _openingPlayback = false; UpdateControlState(); }
    }

    internal bool TryClosePlayback()
    {
        if (_playbackPage is null && !_followRequested) return false;
        _ = ClosePlaybackSafelyAsync();
        return true;
    }

    internal void SetReviewObscured(bool obscured) => _playbackPage?.SetSurfaceObscured(obscured);
    internal void PauseReview()
    {
        if (_playbackPage is not null) _ = ClosePlaybackSafelyAsync();
    }

    private async Task ClosePlaybackSafelyAsync()
    {
        try { await ClosePlaybackAsync(); }
        catch (Exception ex) { ReportFailure("Close review", ex); }
    }

    private async Task ClosePlaybackAsync(bool stopFollowing = true)
    {
        if (stopFollowing) StopFollowing();
        var page = _playbackPage;
        if (page is not null && SelectedSession?.Id == page.Session.Id)
            PlayheadSlider.Value = page.ReviewPosition.TotalSeconds;
        var release = page?.DisposeAsync();
        if (release is { } pendingRelease) _playbackRelease = pendingRelease.AsTask();
        _playbackPage = null;
        PlaybackHost.Content = null;
        PlaybackHost.Visibility = Visibility.Collapsed;
        LibrarySurface.Visibility = Visibility.Visible;
        await _playbackRelease;
        UpdateControlState();
    }

    private async Task OpenMediaFileAsync(string? path, string action)
    {
        if (_openingMediaFile || _disposed) return;
        _openingMediaFile = true;
        UpdateControlState();
        try
        {
            if (path is null || !File.Exists(path)) throw new FileNotFoundException("Video was not found.", path);
            var probe = await _mediaProbe.ProbeAsync(path, _pageCancellation.Token);
            var file = new FileInfo(path);
            var end = new DateTimeOffset(file.LastWriteTimeUtc);
            var id = Guid.NewGuid();
            var segment = new RecordingSegment(Guid.NewGuid(), id, end - probe.Duration, end, string.Empty,
                Path.GetFileNameWithoutExtension(path), probe.VideoCodec, string.Join(" | ", probe.AudioTrackTitles),
                "Existing file", probe.Width, probe.Height, probe.FrameRate, probe.IsHdr, false, path, file.Length);
            var session = new RecordingSession(id, segment.StartTime, end, segment.GameTitle, [segment], false, false);
            await OpenPlaybackAsync(session, TimeSpan.Zero, allowMarkers: false);
        }
        catch (OperationCanceledException) when (_pageCancellation.IsCancellationRequested) { }
        catch (Exception ex) { ReportFailure(action, ex); }
        finally { _openingMediaFile = false; UpdateControlState(); }
    }

    private async void LiveReviewButton_Click(object sender, RoutedEventArgs e)
        => await OpenTimeShiftAsync();

    internal async Task OpenTimeShiftAsync()
    {
        try
        {
            if (!_coordinator.IsRecording) { StatusText.Text = "Start a recording before opening time-shift review."; return; }
            var status = await _recorder.GetRecordingStatusAsync(_pageCancellation.Token);
            if (!status.IsActive) { StatusText.Text = "No active recording to review."; return; }
            _followOrigin = (_recorder as IRecordingTimelineClock)?.RecordingTime is { } time
                ? time - status.Duration : DateTimeOffset.UtcNow - status.Duration;
            StopFollowing();
            _followRequested = true;
            _followWatcher = new FileSystemWatcher(_recordingSettings.RecordingLocation, "*.mkv")
            {
                IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true
            };
            _followWatcher.Created += (_, change) => MarkFollowDirty(change.FullPath);
            _followWatcher.Renamed += (_, change) => MarkFollowDirty(change.FullPath);
            _followWatcher.Error += (_, _) => Interlocked.Exchange(ref _followDirtyAt, Environment.TickCount64);
            Interlocked.Exchange(ref _followDirtyAt, Environment.TickCount64 - 2000);
            _followTimer.Start();
            await RefreshFollowingAsync();
        }
        catch (Exception ex) { StopFollowing(); ReportFailure("Open time-shift review", ex); }
    }

    private void MarkFollowDirty(string path)
    {
        if (!path.EndsWith(".active.mkv", StringComparison.OrdinalIgnoreCase))
            Interlocked.Exchange(ref _followDirtyAt, Environment.TickCount64);
    }

    private void FollowRecordingStateChanged(bool recording)
    {
        if (recording) _ = Dispatcher.InvokeAsync(() => _playbackPage?.MuteForCapture());
        if (!recording && _followRequested) Interlocked.Exchange(ref _followDirtyAt, Environment.TickCount64);
    }

    private async void FollowTimer_Tick(object? sender, EventArgs e)
    {
        try { await RefreshFollowingAsync(); }
        catch (OperationCanceledException) when (_pageCancellation.IsCancellationRequested) { }
        catch (Exception ex) { StopFollowing(); ReportFailure("Follow recording", ex); }
    }

    private async Task RefreshFollowingAsync()
    {
        var dirty = Interlocked.Read(ref _followDirtyAt);
        if (!_followRequested || _refreshingFollow || !IsVisible || dirty == 0 || Environment.TickCount64 - dirty < 1500) return;
        _refreshingFollow = true;
        Interlocked.CompareExchange(ref _followDirtyAt, 0, dirty);
        try
        {
            var sessions = await Task.Run(() => _libraryService.RefreshAsync(cancellationToken: _pageCancellation.Token), _pageCancellation.Token);
            if (!_followRequested || _disposed) return;
            var session = sessions.FirstOrDefault(value => Math.Abs((value.StartTime - _followOrigin).TotalSeconds) <= 5 &&
                !value.HasGaps && !value.HasMissingSegments && !value.HasDamagedSegments);
            if (session is null)
            {
                StatusText.Text = "Waiting for the first finalized segment. Recording continues normally.";
                if (!_coordinator.IsRecording) { StopFollowing(); StatusText.Text = "Recording ended without a finalized segment available for review."; }
                return;
            }
            if (_playbackPage?.Session.Id == session.Id) _playbackPage.AppendSession(session);
            else
            {
                var last = session.Segments.MaxBy(static segment => segment.StartTime)!;
                var buffer = last.EndTime - last.StartTime;
                await OpenPlaybackAsync(session, session.Duration > buffer ? session.Duration - buffer : TimeSpan.Zero, follow: true);
            }
            if (!_coordinator.IsRecording) StopFollowing();
        }
        finally { _refreshingFollow = false; }
    }

    private void StopFollowing()
    {
        _followRequested = false;
        _followTimer.Stop();
        _followWatcher?.Dispose();
        _followWatcher = null;
        if (_playbackPage is not null) _playbackPage.FollowRecording = false;
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
                contextBefore: InstantClipDurationParser.Parse(
                    ((ComboBoxItem)MarkerContextBeforeComboBox.SelectedItem).Content.ToString()!),
                contextAfter: InstantClipDurationParser.Parse(
                    ((ComboBoxItem)MarkerContextAfterComboBox.SelectedItem).Content.ToString()!),
                cancellationToken: _pageCancellation.Token);
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
            await _libraryService.DeleteMarkerAsync(markerId, _pageCancellation.Token);
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
                _pageCancellation.Token);
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
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
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
        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_pageCancellation.Token);
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

    private async void OpenExportButton_Click(object sender, RoutedEventArgs e) =>
        await OpenMediaFileAsync(_lastExportPath, "Open export");

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
        catch (OperationCanceledException) when (_pageCancellation.IsCancellationRequested)
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
        WaveformTimeline.RefreshCursor();
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
        PlayButton.IsEnabled = !_isBusy && !_openingMediaFile && !_openingPlayback && healthy;
        ExportButton.IsEnabled = !_isBusy && healthy;
        FullSessionButton.IsEnabled = !_isBusy && hasSession;
        AddMarkerButton.IsEnabled = !_isBusy && hasSession;
        ProtectSelectionButton.IsEnabled = !_isBusy && healthy;
        SelectionStartSlider.IsEnabled = !_isBusy && hasSession;
        SelectionEndSlider.IsEnabled = !_isBusy && hasSession;
        PlayheadSlider.IsEnabled = !_isBusy && healthy;
        ExportPresetComboBox.IsEnabled = !_isBusy && healthy;
        AudioTrackItemsControl.IsEnabled = !_isBusy && healthy;
        PlayButton.Content = _playbackPage is null ? "Review from cursor" : "Show review";
        UpdateClipControls();
        UpdateNoteControls();
    }

    private void UpdateClipControls()
    {
        var clip = SelectedClip;
        var playable = clip?.IsPlayable == true && File.Exists(clip.OutputPath);
        RefreshClipsButton.IsEnabled = !_isBusy && !_isRefreshingClips;
        ClipListBox.IsEnabled = !_isBusy && !_isRefreshingClips;
        OpenClipButton.IsEnabled = !_isBusy && playable;
        OpenClipSourceButton.IsEnabled = !_isBusy && clip?.SessionId is not null;
        ProtectClipButton.IsEnabled = !_isBusy &&
            clip?.Status is InstantClipStatus.Ready or InstantClipStatus.Protected;
        ProtectClipButton.Content =
            clip?.Status == InstantClipStatus.Protected ? "Unprotect" : "Protect";
        RetryClipButton.IsEnabled = !_isBusy && clip?.Status == InstantClipStatus.Failed;
        ExportedClipButton.IsEnabled = !_isBusy &&
            playable &&
            clip?.Status != InstantClipStatus.Exported;
        DiscardClipButton.IsEnabled = !_isBusy && clip is not null;
        ClipNoteTextBox.IsEnabled = !_isBusy && clip is not null;
        SaveClipNoteButton.IsEnabled = !_isBusy && clip is not null;
        DeleteClipNoteButton.IsEnabled = !_isBusy && _selectedClipNote is not null;
    }

    private void UpdateNoteControls()
    {
        var hasTarget = TryGetNoteTarget(out _, out _, out _, out _);
        NoteTargetComboBox.IsEnabled = !_isBusy && SelectedSession is not null;
        NoteMarkerComboBox.IsEnabled = !_isBusy &&
            SelectedNoteTarget?.Value == RecordingNoteTarget.Marker &&
            NoteMarkerComboBox.Items.Count > 0;
        NoteListBox.IsEnabled = !_isBusy && hasTarget;
        NoteTextBox.IsEnabled = !_isBusy && hasTarget;
        SaveNoteButton.IsEnabled = !_isBusy && hasTarget;
        NewNoteButton.IsEnabled = !_isBusy && hasTarget;
        DeleteNoteButton.IsEnabled = !_isBusy && _selectedNote is not null;
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
    private InstantClip? SelectedClip => (ClipListBox.SelectedItem as ClipListItem)?.Clip;
    private NoteTargetOption? SelectedNoteTarget =>
        NoteTargetComboBox.SelectedItem as NoteTargetOption;

    private static ImageSource LoadImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 240;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    private static string FormatCompactDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes:0.#}m"
            : $"{duration.TotalSeconds:0.#}s";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.RecordingStateChanged -= FollowRecordingStateChanged;
        _instantClipService.ClipsChanged -= InstantClipService_ClipsChanged;
        StopFollowing();
        if (_playbackPage is not null) _ = _playbackPage.DisposeAsync();
        _playbackPage = null;
        _pageCancellation.Cancel();
        _timelineAssetCancellation?.Cancel();
        _exportCancellation?.Cancel();
        _timelineAssetCancellation?.Dispose();
        _exportCancellation?.Dispose();
        ThumbnailItemsControl.ItemsSource = null;
        PreviewImage.Source = null;
        WaveformTimeline.Samples = [];
        _pageCancellation.Dispose();
    }

    private sealed class SessionListItem(RecordingSession session)
    {
        public RecordingSession Session { get; set; } = session;
        public string Title => Session.GameTitle == "Recording" ? "Continuous recording" : Session.GameTitle;
        public string RecordedAt => Session.StartTime.LocalDateTime.ToString("g");
        public DateTimeOffset SortTime => Session.StartTime;
        public TimeSpan SortDuration => Session.Duration;
        public string Summary =>
            $"{FormatDuration(Session.Duration)} | {Session.Segments.Count} segment(s)" +
            (Session.HasGaps || Session.HasMissingSegments || Session.HasDamagedSegments ? " | Needs attention" : string.Empty);
    }

    private sealed record ThumbnailItem(TimeSpan Offset, string Time, ImageSource Image);
    private sealed record MarkerItem(Guid Id, string Time, string Label, string Context);
    private sealed record NoteTargetOption(RecordingNoteTarget Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class NoteListItem(RecordingNote note)
    {
        public RecordingNote Note { get; } = note;
        public string Text => Note.Text;
        public string UpdatedAt => Note.UpdatedAt.LocalDateTime.ToString("g");
        public string Detail => Note.RangeStart is null
            ? Note.Target.ToString()
            : $"{FormatDuration(Note.RangeStart.Value)} - {FormatDuration(Note.RangeEnd!.Value)}";
    }
    private sealed class ClipListItem(InstantClip clip)
    {
        public InstantClip Clip { get; } = clip;
        public string Label => Clip.Label;
        public string Status => Clip.Status.ToString();
        public string Detail =>
            $"{Clip.RequestedAt.LocalDateTime:g} | requested {FormatDuration(Clip.RequestedDuration)}" +
            (Clip.ActualDuration is null
                ? string.Empty
                : $" | saved {FormatDuration(Clip.ActualDuration.Value)}") +
            (string.IsNullOrWhiteSpace(Clip.GameTitle)
                ? string.Empty
                : $" | {Clip.GameTitle}");
        public string Failure => Clip.FailureDetail ?? string.Empty;
    }
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
