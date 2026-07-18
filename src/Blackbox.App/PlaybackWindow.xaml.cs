using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Blackbox.Domain;
using Blackbox.Export;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WPF window synchronously releases VLC, media, leases, and cancellation state from its Closing event.")]
public partial class PlaybackWindow : Window
{
    private static readonly IReadOnlyList<PlaybackRateOption> PlaybackRates =
    [
        new(0.25f),
        new(0.5f),
        new(0.75f),
        new(1f),
        new(1.25f),
        new(1.5f),
        new(2f)
    ];

    private readonly RecordingLibraryService _libraryService;
    private readonly ILogger<PlaybackWindow> _logger;
    private readonly IReadOnlyList<RecordingSegment> _segments;
    private readonly IReadOnlyList<TimeSpan> _segmentStarts;
    private readonly TimeSpan _initialOffset;
    private readonly IDisposable _segmentLease;
    private readonly CancellationTokenSource _windowCancellation = new();
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly DispatcherTimer _positionTimer;
    private RecordingSession _session;
    private Media? _activeMedia;
    private int _activeSegmentIndex = -1;
    private long? _pendingSegmentTimeMilliseconds;
    private bool _pauseWhenReady;
    private bool _closing;
    private bool _fullscreen;
    private bool _markerOperationInFlight;
    private bool _updatingAudioTracks;
    private bool _frameStepPumpRunning;
    private bool _frameStepMode;
    private int? _selectedAudioTrackId;
    private int _queuedFrameSteps;
    private CancellationTokenSource? _frameStepCancellation;
    private float _playbackRate = 1f;
    private TimeSpan _lastKnownPosition;
    private WindowStyle _restoredWindowStyle;
    private WindowState _restoredWindowState;
    private ResizeMode _restoredResizeMode;

    public PlaybackWindow(
        RecordingSession session,
        TimeSpan initialOffset,
        RecordingLibraryService libraryService,
        ISegmentUsageRegistry segmentUsageRegistry,
        ILogger<PlaybackWindow> logger)
    {
        ValidateSession(session);
        _session = session;
        _libraryService = libraryService;
        _logger = logger;
        _segments = session.Segments.OrderBy(static segment => segment.StartTime).ToArray();
        _segmentStarts = RecordingTimeline.GetSegmentStartOffsets(session);
        _initialOffset = ClampOffset(initialOffset);
        _lastKnownPosition = _initialOffset;

        InitializeComponent();
        Core.Initialize();
        _libVlc = new LibVLC("--quiet", "--no-video-title-show", "--no-snapshot-preview");
        _mediaPlayer = new MediaPlayer(_libVlc)
        {
            EnableKeyInput = false,
            EnableMouseInput = false,
            Volume = 100
        };
        _mediaPlayer.Playing += MediaPlayer_Playing;
        _mediaPlayer.Paused += MediaPlayer_StateChanged;
        _mediaPlayer.Stopped += MediaPlayer_StateChanged;
        _mediaPlayer.EndReached += MediaPlayer_EndReached;
        _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
        VideoView.MediaPlayer = _mediaPlayer;
        _segmentLease = segmentUsageRegistry.Acquire(_segments.Select(static segment => segment.Id).ToArray());

        _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _positionTimer.Tick += PositionTimer_Tick;
        ConfigureSurface();
    }

    public RecordingSession Session => _session;
    public event EventHandler? MarkersChanged;

    public void ShowAt(TimeSpan offset)
    {
        if (_closing)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Seek(offset, playAfterSeek: true);
        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    private void ConfigureSurface()
    {
        SessionTitleText.Text = _session.GameTitle == "Recording"
            ? $"Recording {_session.StartTime.LocalDateTime:g}"
            : _session.GameTitle;
        PlaybackStatusText.Text = $"{_segments[0].Width} x {_segments[0].Height}  |  {_segments[0].FrameRate:0.##} fps";
        DurationText.Text = FormatPrecise(_session.Duration);
        PlaybackTimeline.Duration = _session.Duration;
        PlaybackTimeline.SegmentBoundaries = RecordingTimeline.GetSegmentBoundaries(_session);
        PlaybackTimeline.Markers = _session.Markers;
        PlaybackTimeline.CursorPosition = _initialOffset;
        PlaybackTimeline.ScrubRequested += PlaybackTimeline_ScrubRequested;
        PlaybackRateComboBox.ItemsSource = PlaybackRates;
        PlaybackRateComboBox.SelectedItem = PlaybackRates.Single(static option => option.Rate == 1f);
        RefreshMarkers();
        UpdatePositionDisplay(_initialOffset);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Seek(_initialOffset, playAfterSeek: true);
            _positionTimer.Start();
        }
        catch (Exception ex)
        {
            ReportPlaybackFailure(ex);
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void BackTenButton_Click(object sender, RoutedEventArgs e) =>
        Seek(CurrentOffset() - TimeSpan.FromSeconds(10), _mediaPlayer.IsPlaying);

    private void ForwardTenButton_Click(object sender, RoutedEventArgs e) =>
        Seek(CurrentOffset() + TimeSpan.FromSeconds(10), _mediaPlayer.IsPlaying);

    private void StartButton_Click(object sender, RoutedEventArgs e) => Seek(TimeSpan.Zero, _mediaPlayer.IsPlaying);

    private void EndButton_Click(object sender, RoutedEventArgs e) =>
        Seek(_session.Duration - TimeSpan.FromMilliseconds(1), playAfterSeek: false);

    private void PreviousSegmentButton_Click(object sender, RoutedEventArgs e) => PreviousSegment();

    private void NextSegmentButton_Click(object sender, RoutedEventArgs e) => NextSegment();

    private void PreviousFrameButton_Click(object sender, RoutedEventArgs e) => StepFrame(-1);

    private void NextFrameButton_Click(object sender, RoutedEventArgs e) => StepFrame(1);

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.Mute = !_mediaPlayer.Mute;
        UpdateMuteGlyph();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || _mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.Volume = (int)Math.Round(e.NewValue);
        if (e.NewValue > 0 && _mediaPlayer.Mute)
        {
            _mediaPlayer.Mute = false;
        }

        UpdateMuteGlyph();
    }

    private void PlaybackRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaybackRateComboBox.SelectedItem is not PlaybackRateOption option)
        {
            return;
        }

        _playbackRate = option.Rate;
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.SetRate(_playbackRate);
        }
    }

    private void AudioTrackComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAudioTracks || AudioTrackComboBox.SelectedItem is not AudioTrackOption option)
        {
            return;
        }

        if (_mediaPlayer.SetAudioTrack(option.Id))
        {
            _selectedAudioTrackId = option.Id;
        }
    }

    private void PlaybackTimeline_ScrubRequested(object? sender, TimeSpan offset) =>
        Seek(offset, _mediaPlayer.IsPlaying);

    private async void QuickTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string label })
        {
            await AddMarkerAsync(label);
        }
    }

    private async void AddManualMarkerButton_Click(object sender, RoutedEventArgs e) =>
        await AddManualMarkerAsync();

    private async void MarkerLabelTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await AddManualMarkerAsync();
        }
    }

    private void MarkerListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MarkerListBox.SelectedItem is MarkerItem marker)
        {
            Seek(marker.Offset, _mediaPlayer.IsPlaying);
        }
    }

    private void PreviousMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        var position = CurrentOffset() - TimeSpan.FromMilliseconds(50);
        var marker = _session.Markers
            .Where(value => value.Offset < position)
            .OrderByDescending(static value => value.Offset)
            .FirstOrDefault();
        if (marker is not null)
        {
            Seek(marker.Offset, _mediaPlayer.IsPlaying);
            SelectMarker(marker.Id);
        }
    }

    private void NextMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        var position = CurrentOffset() + TimeSpan.FromMilliseconds(50);
        var marker = _session.Markers
            .Where(value => value.Offset > position)
            .OrderBy(static value => value.Offset)
            .FirstOrDefault();
        if (marker is not null)
        {
            Seek(marker.Offset, _mediaPlayer.IsPlaying);
            SelectMarker(marker.Id);
        }
    }

    private async void DeleteMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_markerOperationInFlight || MarkerListBox.SelectedItem is not MarkerItem selected)
        {
            return;
        }

        _markerOperationInFlight = true;
        try
        {
            await _libraryService.DeleteMarkerAsync(selected.Id, _windowCancellation.Token);
            _session = _session with
            {
                Markers = _session.Markers.Where(marker => marker.Id != selected.Id).ToArray()
            };
            RefreshMarkers();
            MarkersChanged?.Invoke(this, EventArgs.Empty);
            MarkerStatusText.Text = "Marker removed.";
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportMarkerFailure("Remove marker", ex);
        }
        finally
        {
            _markerOperationInFlight = false;
        }
    }

    private async Task AddManualMarkerAsync()
    {
        var label = string.IsNullOrWhiteSpace(MarkerLabelTextBox.Text)
            ? "Event"
            : MarkerLabelTextBox.Text.Trim();
        await AddMarkerAsync(label);
        MarkerLabelTextBox.Clear();
    }

    private async Task AddMarkerAsync(string label)
    {
        if (_markerOperationInFlight)
        {
            return;
        }

        _markerOperationInFlight = true;
        try
        {
            var marker = await _libraryService.AddMarkerAsync(
                _session,
                CurrentOffset(),
                label,
                _windowCancellation.Token);
            _session = _session with
            {
                Markers = _session.Markers.Append(marker).OrderBy(static value => value.Offset).ToArray()
            };
            RefreshMarkers();
            SelectMarker(marker.Id);
            MarkersChanged?.Invoke(this, EventArgs.Empty);
            MarkerStatusText.Text = $"{label} tagged at {FormatPrecise(marker.Offset)}.";
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportMarkerFailure("Add marker", ex);
        }
        finally
        {
            _markerOperationInFlight = false;
        }
    }

    private void RefreshMarkers()
    {
        PlaybackTimeline.Markers = _session.Markers;
        PlaybackTimeline.Refresh();
        MarkerListBox.ItemsSource = _session.Markers
            .OrderBy(static marker => marker.Offset)
            .Select(static marker => new MarkerItem(marker.Id, marker.Offset, FormatPrecise(marker.Offset), marker.Label))
            .ToArray();
    }

    private void SelectMarker(Guid markerId)
    {
        var item = MarkerListBox.Items.OfType<MarkerItem>().FirstOrDefault(marker => marker.Id == markerId);
        if (item is null)
        {
            return;
        }

        MarkerListBox.SelectedItem = item;
        MarkerListBox.ScrollIntoView(item);
    }

    private void TogglePlayPause()
    {
        if (_mediaPlayer.IsPlaying)
        {
            CancelFrameNavigation();
            _mediaPlayer.SetPause(true);
        }
        else if (_frameStepMode)
        {
            Seek(_lastKnownPosition, playAfterSeek: true);
        }
        else if (_mediaPlayer.State == VLCState.Paused)
        {
            _mediaPlayer.SetPause(false);
        }
        else
        {
            Seek(CurrentOffset(), playAfterSeek: true);
        }

        UpdatePlayPauseGlyph();
    }

    private void PreviousSegment()
    {
        var current = CurrentOffset();
        var location = RecordingTimeline.LocateSegment(_session, current);
        var segmentStart = _segmentStarts[location.SegmentIndex];
        var targetIndex = current - segmentStart > TimeSpan.FromSeconds(1)
            ? location.SegmentIndex
            : Math.Max(0, location.SegmentIndex - 1);
        Seek(_segmentStarts[targetIndex], _mediaPlayer.IsPlaying);
    }

    private void NextSegment()
    {
        var location = RecordingTimeline.LocateSegment(_session, CurrentOffset());
        if (location.SegmentIndex + 1 < _segments.Count)
        {
            Seek(_segmentStarts[location.SegmentIndex + 1], _mediaPlayer.IsPlaying);
        }
        else
        {
            Seek(_session.Duration - TimeSpan.FromMilliseconds(1), playAfterSeek: false);
        }
    }

    private void StepFrame(int direction)
    {
        if (_closing || direction == 0)
        {
            return;
        }

        _queuedFrameSteps = Math.Clamp(_queuedFrameSteps + Math.Sign(direction), -24, 24);
        if (!_frameStepPumpRunning)
        {
            _ = ProcessFrameStepsAsync();
        }
    }

    private async Task ProcessFrameStepsAsync()
    {
        _frameStepPumpRunning = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        _frameStepCancellation = cancellation;
        try
        {
            if (_mediaPlayer.IsPlaying)
            {
                _lastKnownPosition = CurrentOffset();
                _mediaPlayer.SetPause(true);
                await WaitForPlayingStateAsync(playing: false, TimeSpan.FromMilliseconds(150), cancellation.Token);
            }

            _frameStepMode = true;
            while (_queuedFrameSteps != 0)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var direction = Math.Sign(_queuedFrameSteps);
                _queuedFrameSteps -= direction;
                StepSingleFrame(direction);
                await Task.Delay(5, cancellation.Token);
            }

            if (!_closing)
            {
                await RefreshPausedFrameAsync(
                    _lastKnownPosition,
                    CurrentFrameDuration(),
                    cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportPlaybackFailure(ex);
        }
        finally
        {
            if (ReferenceEquals(_frameStepCancellation, cancellation))
            {
                _frameStepCancellation = null;
            }

            _frameStepPumpRunning = false;
            if (!_closing && _queuedFrameSteps != 0)
            {
                _ = ProcessFrameStepsAsync();
            }
        }
    }

    private void StepSingleFrame(int direction)
    {
        var current = ClampOffset(_lastKnownPosition);
        var frameDuration = CurrentFrameDuration();
        var target = ClampOffset(current + TimeSpan.FromTicks(frameDuration.Ticks * direction));
        if (target == current)
        {
            UpdatePositionDisplay(target);
            return;
        }

        _lastKnownPosition = target;
        _frameStepMode = true;
        UpdatePositionDisplay(target);
    }

    private async Task RefreshPausedFrameAsync(
        TimeSpan target,
        TimeSpan frameDuration,
        CancellationToken cancellationToken)
    {
        var restoreVolume = (int)Math.Round(VolumeSlider.Value);
        var restoreMute = _mediaPlayer.Mute;
        _mediaPlayer.Volume = 0;
        try
        {
            _frameStepMode = false;
            _lastKnownPosition = target;
            var location = RecordingTimeline.LocateSegment(_session, target);
            PlaySegment(location.SegmentIndex, location.SegmentOffset, playAfterSeek: true);
            var startedAt = Stopwatch.GetTimestamp();
            long? playbackStartedAt = null;
            var resumeRetryCount = 0;
            var minimumPresentationTime = TimeSpan.FromMilliseconds(
                Math.Clamp(frameDuration.TotalMilliseconds * 5, 150, 220));
            while (Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromMilliseconds(350))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_mediaPlayer.IsPlaying)
                {
                    playbackStartedAt ??= Stopwatch.GetTimestamp();
                    var decodedPosition = _activeSegmentIndex < 0 || _mediaPlayer.Time < 0
                        ? target
                        : _segmentStarts[_activeSegmentIndex] + TimeSpan.FromMilliseconds(_mediaPlayer.Time);
                    if (Stopwatch.GetElapsedTime(playbackStartedAt.Value) >= minimumPresentationTime &&
                        decodedPosition >= target - TimeSpan.FromTicks(frameDuration.Ticks / 3))
                    {
                        break;
                    }
                }
                else
                {
                    var elapsed = Stopwatch.GetElapsedTime(startedAt);
                    if (resumeRetryCount == 0 && elapsed >= TimeSpan.FromMilliseconds(75))
                    {
                        _mediaPlayer.SetPause(false);
                        resumeRetryCount++;
                    }
                    else if (resumeRetryCount == 1 && elapsed >= TimeSpan.FromMilliseconds(160))
                    {
                        _mediaPlayer.Play();
                        resumeRetryCount++;
                    }
                }

                await Task.Delay(5, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.SetPause(true);
                await WaitForPlayingStateAsync(
                    playing: false,
                    TimeSpan.FromMilliseconds(150),
                    cancellationToken);
            }
            else
            {
                _pauseWhenReady = true;
            }

            _lastKnownPosition = target;
            _frameStepMode = true;
            UpdatePositionDisplay(target);
        }
        finally
        {
            if (!_closing)
            {
                _mediaPlayer.Volume = restoreVolume;
                _mediaPlayer.Mute = restoreMute;
                UpdateMuteGlyph();
            }
        }
    }

    private async Task WaitForPlayingStateAsync(
        bool playing,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (_mediaPlayer.IsPlaying != playing && Stopwatch.GetElapsedTime(startedAt) < timeout)
        {
            await Task.Delay(5, cancellationToken);
        }
    }

    private void Seek(
        TimeSpan requestedOffset,
        bool playAfterSeek,
        bool preserveFrameNavigation = false,
        bool publishPosition = true)
    {
        if (_closing)
        {
            return;
        }

        if (!preserveFrameNavigation)
        {
            CancelFrameNavigation();
        }

        var offset = ClampOffset(requestedOffset);
        var location = RecordingTimeline.LocateSegment(_session, offset);
        if (publishPosition)
        {
            _lastKnownPosition = offset;
        }

        if (location.SegmentIndex != _activeSegmentIndex || _activeMedia is null)
        {
            PlaySegment(location.SegmentIndex, location.SegmentOffset, playAfterSeek);
        }
        else
        {
            _mediaPlayer.Time = Math.Max(0, (long)location.SegmentOffset.TotalMilliseconds);
            _mediaPlayer.SetRate(_playbackRate);
            if (playAfterSeek)
            {
                if (_mediaPlayer.State == VLCState.Paused)
                {
                    _mediaPlayer.SetPause(false);
                }
                else if (!_mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Play();
                }
            }
            else if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.SetPause(true);
            }
        }

        if (publishPosition)
        {
            UpdatePositionDisplay(offset);
        }
    }

    private void CancelFrameNavigation()
    {
        _queuedFrameSteps = 0;
        _frameStepMode = false;
        _frameStepCancellation?.Cancel();
    }

    private void PlaySegment(int segmentIndex, TimeSpan segmentOffset, bool playAfterSeek)
    {
        if (_activeMedia is not null)
        {
            _mediaPlayer.Stop();
            _activeMedia.Dispose();
        }

        _activeSegmentIndex = segmentIndex;
        _pendingSegmentTimeMilliseconds = Math.Max(0, (long)segmentOffset.TotalMilliseconds);
        _pauseWhenReady = !playAfterSeek;
        _activeMedia = new Media(_libVlc, new Uri(_segments[segmentIndex].FilePath));
        _mediaPlayer.Media = _activeMedia;
        if (!_mediaPlayer.Play())
        {
            throw new InvalidOperationException($"VLC could not play {Path.GetFileName(_segments[segmentIndex].FilePath)}.");
        }
    }

    private void MediaPlayer_Playing(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing)
            {
                return;
            }

            if (_pendingSegmentTimeMilliseconds is { } milliseconds)
            {
                _mediaPlayer.Time = milliseconds;
                _pendingSegmentTimeMilliseconds = null;
            }

            _mediaPlayer.SetRate(_playbackRate);
            RefreshAudioTracks();
            if (_pauseWhenReady)
            {
                _pauseWhenReady = false;
                _mediaPlayer.SetPause(true);
            }

            UpdatePlayPauseGlyph();
        });
    }

    private void MediaPlayer_StateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(UpdatePlayPauseGlyph);

    private void MediaPlayer_EndReached(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing)
            {
                return;
            }

            if (_activeSegmentIndex + 1 < _segments.Count)
            {
                PlaySegment(_activeSegmentIndex + 1, TimeSpan.Zero, playAfterSeek: true);
            }
            else if (LoopCheckBox.IsChecked == true)
            {
                Seek(TimeSpan.Zero, playAfterSeek: true);
            }
            else
            {
                _lastKnownPosition = _session.Duration;
                UpdatePositionDisplay(_lastKnownPosition);
                UpdatePlayPauseGlyph();
            }
        });
    }

    private void MediaPlayer_EncounteredError(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => ReportPlaybackFailure(
            new InvalidOperationException("VLC could not decode this recording segment.")));

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        if (_frameStepPumpRunning || (_frameStepMode && !_mediaPlayer.IsPlaying))
        {
            UpdatePositionDisplay(_lastKnownPosition);
            return;
        }

        _lastKnownPosition = CurrentOffset();
        UpdatePositionDisplay(_lastKnownPosition);
    }

    private TimeSpan CurrentOffset()
    {
        if (_frameStepMode && !_mediaPlayer.IsPlaying)
        {
            return ClampOffset(_lastKnownPosition);
        }

        if (_activeSegmentIndex < 0 || _mediaPlayer.Time < 0)
        {
            return ClampOffset(_lastKnownPosition);
        }

        return ClampOffset(
            _segmentStarts[_activeSegmentIndex] + TimeSpan.FromMilliseconds(_mediaPlayer.Time));
    }

    private void UpdatePositionDisplay(TimeSpan offset)
    {
        var clamped = offset < TimeSpan.Zero
            ? TimeSpan.Zero
            : offset > _session.Duration
                ? _session.Duration
                : offset;
        PlaybackTimeline.CursorPosition = clamped;
        PlaybackTimeline.Refresh();
        CurrentTimeText.Text = FormatPrecise(clamped);
        var location = RecordingTimeline.LocateSegment(_session, clamped);
        SegmentStatusText.Text = $"Segment {location.SegmentIndex + 1} of {_segments.Count}";
        var nearbyMarker = _session.Markers
            .MinBy(marker => Math.Abs((marker.Offset - clamped).Ticks));
        CurrentMarkerText.Text = nearbyMarker is not null &&
                                 Math.Abs((nearbyMarker.Offset - clamped).TotalSeconds) <= 0.4
            ? nearbyMarker.Label
            : string.Empty;
        UpdatePlayPauseGlyph();
    }

    private TimeSpan CurrentFrameDuration()
    {
        var frameRate = _mediaPlayer.Fps > 0
            ? (double)_mediaPlayer.Fps
            : _activeSegmentIndex >= 0 && _segments[_activeSegmentIndex].FrameRate > 0
                ? (double)_segments[_activeSegmentIndex].FrameRate
                : 30;
        return TimeSpan.FromSeconds(1d / frameRate);
    }

    private TimeSpan ClampOffset(TimeSpan offset)
    {
        var maximum = _session.Duration > TimeSpan.FromMilliseconds(1)
            ? _session.Duration - TimeSpan.FromMilliseconds(1)
            : TimeSpan.Zero;
        return offset < TimeSpan.Zero ? TimeSpan.Zero : offset > maximum ? maximum : offset;
    }

    private void UpdatePlayPauseGlyph()
    {
        if (!IsInitialized)
        {
            return;
        }

        PlayPauseGlyph.Text = _mediaPlayer.IsPlaying ? "\uE769" : "\uE768";
        PlaybackStatusText.Text = _mediaPlayer.IsPlaying
            ? $"Playing at {_playbackRate:0.##}x"
            : _mediaPlayer.State == VLCState.Paused
                ? "Paused"
                : "Ready";
    }

    private void UpdateMuteGlyph()
    {
        if (!IsInitialized)
        {
            return;
        }

        MuteGlyph.Text = _mediaPlayer.Mute || _mediaPlayer.Volume == 0 ? "\uE74F" : "\uE767";
    }

    private void RefreshAudioTracks()
    {
        var options = _mediaPlayer.AudioTrackDescription
            .Select(static track => new AudioTrackOption(track.Id, track.Name))
            .ToArray();
        var selectedId = _selectedAudioTrackId ?? _mediaPlayer.AudioTrack;
        _updatingAudioTracks = true;
        try
        {
            AudioTrackComboBox.ItemsSource = options;
            AudioTrackComboBox.SelectedItem = options.FirstOrDefault(option => option.Id == selectedId)
                                                   ?? options.FirstOrDefault(option => option.Id == _mediaPlayer.AudioTrack)
                                                   ?? options.FirstOrDefault();
            AudioTrackComboBox.IsEnabled = options.Length > 1;
        }
        finally
        {
            _updatingAudioTracks = false;
        }

        if (AudioTrackComboBox.SelectedItem is AudioTrackOption selected)
        {
            _selectedAudioTrackId = selected.Id;
            _mediaPlayer.SetAudioTrack(selected.Id);
        }
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _restoredWindowStyle = WindowStyle;
            _restoredWindowState = WindowState;
            _restoredResizeMode = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _fullscreen = true;
        }
        else
        {
            WindowStyle = _restoredWindowStyle;
            ResizeMode = _restoredResizeMode;
            WindowState = _restoredWindowState;
            _fullscreen = false;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                TogglePlayPause();
                break;
            case Key.Left when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                PreviousSegment();
                break;
            case Key.Right when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                NextSegment();
                break;
            case Key.Left:
                Seek(CurrentOffset() - TimeSpan.FromSeconds(10), _mediaPlayer.IsPlaying);
                break;
            case Key.Right:
                Seek(CurrentOffset() + TimeSpan.FromSeconds(10), _mediaPlayer.IsPlaying);
                break;
            case Key.OemComma:
                StepFrame(-1);
                break;
            case Key.OemPeriod:
                StepFrame(1);
                break;
            case Key.Home:
                Seek(TimeSpan.Zero, _mediaPlayer.IsPlaying);
                break;
            case Key.End:
                Seek(_session.Duration - TimeSpan.FromMilliseconds(1), playAfterSeek: false);
                break;
            case Key.M:
                _mediaPlayer.Mute = !_mediaPlayer.Mute;
                UpdateMuteGlyph();
                break;
            case Key.F11:
                ToggleFullscreen();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ReportPlaybackFailure(Exception exception)
    {
        _logger.LogError(exception, "Blackbox playback failed for session {RecordingSessionId}.", _session.Id);
        PlaybackStatusText.Text = $"Playback failed: {exception.Message}";
        PlayPauseButton.IsEnabled = false;
    }

    private void ReportMarkerFailure(string action, Exception exception)
    {
        _logger.LogError(exception, "{MarkerAction} failed for session {RecordingSessionId}.", action, _session.Id);
        MarkerStatusText.Text = $"{action} failed: {exception.Message}";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _frameStepCancellation?.Cancel();
        _windowCancellation.Cancel();
        _positionTimer.Stop();
        PlaybackTimeline.ScrubRequested -= PlaybackTimeline_ScrubRequested;
        _mediaPlayer.Playing -= MediaPlayer_Playing;
        _mediaPlayer.Paused -= MediaPlayer_StateChanged;
        _mediaPlayer.Stopped -= MediaPlayer_StateChanged;
        _mediaPlayer.EndReached -= MediaPlayer_EndReached;
        _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;
        VideoView.MediaPlayer = null;
        _mediaPlayer.Stop();
        _activeMedia?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        _segmentLease.Dispose();
        _windowCancellation.Dispose();
    }

    private static void ValidateSession(RecordingSession session)
    {
        if (session.Segments.Count == 0)
        {
            throw new InvalidOperationException("This recording does not contain any playable segments.");
        }

        if (session.HasGaps || session.HasMissingSegments || session.HasDamagedSegments)
        {
            throw new InvalidOperationException("This recording has a missing or damaged section and cannot play continuously.");
        }

        var missingFile = session.Segments.FirstOrDefault(segment => !File.Exists(segment.FilePath));
        if (missingFile is not null)
        {
            throw new FileNotFoundException("A recording segment could not be found.", missingFile.FilePath);
        }
    }

    private static string FormatPrecise(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss\.fff")
            : duration.ToString(@"m\:ss\.fff");

    private sealed record PlaybackRateOption(float Rate)
    {
        public override string ToString() => $"{Rate:0.##}x";
    }

    private sealed record AudioTrackOption(int Id, string Name)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(Name) ? $"Track {Id}" : Name;
    }

    private sealed record MarkerItem(Guid Id, TimeSpan Offset, string Time, string Label);
}
