using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Blackbox.Domain;
using Blackbox.Export;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The owning library releases the page asynchronously.")]
public partial class PlaybackPage : UserControl, IAsyncDisposable
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
    private readonly ILogger<PlaybackPage> _logger;
    private PlaybackTimelineIndex _timelineIndex;
    private IReadOnlyList<RecordingSegment> _segments => _timelineIndex.Segments;
    private IReadOnlyList<TimeSpan> _segmentStarts => _timelineIndex.StartOffsets;
    private readonly PlaybackRuntime _runtime;
    private readonly TimeSpan _initialOffset;
    private readonly PlaybackSegmentLease _segmentLease;
    private readonly CancellationTokenSource _windowCancellation = new();
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _seekTimer;
    private TimeSpan? _pendingScrub;
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
    private bool _started;
    private readonly bool _allowMarkers;
    private bool _waitingForMore;
    private Task? _disposeTask;
    private PlaybackVideoBackground? _videoBackground;
    private StreamMediaInput? _continuousInput;
    private MatroskaPlaybackStream? _continuousStream;
    private int _continuousSegmentCount;
    internal int MediaOpenCount { get; private set; }
    internal int SeekDispatchCount { get; private set; }
    internal event Action? SegmentSurfaceExposed;

    public PlaybackPage(
        RecordingSession session,
        TimeSpan initialOffset,
        RecordingLibraryService libraryService,
        ISegmentUsageRegistry segmentUsageRegistry,
        ILogger<PlaybackPage> logger,
        PlaybackRuntime runtime,
        LibVLC libVlc,
        bool allowMarkers = true,
        bool startMuted = false)
    {
        ValidateSession(session);
        _session = session;
        _libraryService = libraryService;
        _logger = logger;
        _timelineIndex = new PlaybackTimelineIndex(session);
        _runtime = runtime;
        _allowMarkers = allowMarkers;
        _initialOffset = ClampOffset(initialOffset);
        _lastKnownPosition = _initialOffset;

        InitializeComponent();
        _libVlc = libVlc;
        _mediaPlayer = new MediaPlayer(_libVlc)
        {
            EnableKeyInput = false,
            EnableMouseInput = false,
            EnableHardwareDecoding = true,
            FileCaching = 150,
            Volume = 100
        };
        _mediaPlayer.Playing += MediaPlayer_Playing;
        _mediaPlayer.Paused += MediaPlayer_StateChanged;
        _mediaPlayer.Stopped += MediaPlayer_StateChanged;
        _mediaPlayer.EndReached += MediaPlayer_EndReached;
        _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
        VideoView.MediaPlayer = _mediaPlayer;
        _segmentLease = new PlaybackSegmentLease(segmentUsageRegistry);
        _segmentLease.Add(_segments);

        _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _positionTimer.Tick += PositionTimer_Tick;
        _seekTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _seekTimer.Tick += (_, _) =>
        {
            _seekTimer.Stop();
            if (_pendingScrub is { } offset && !_closing)
            {
                _pendingScrub = null;
                Seek(offset, _mediaPlayer.IsPlaying);
            }
        };
        ConfigureSurface();
        if (startMuted) MuteForCapture();
        MomentsPanel.IsEnabled = allowMarkers;
        if (!allowMarkers) MarkerStatusText.Text = "Clip or export preview";
    }

    public RecordingSession Session => _session;
    internal TimeSpan ReviewPosition => _lastKnownPosition;
    public event EventHandler? MarkersChanged;
    private bool _followRecording;
    public bool FollowRecording
    {
        get => _followRecording;
        set
        {
            _followRecording = value;
            LiveStatusText.Text = value ? "Time-shift | finalized segments" : string.Empty;
            LiveStatusText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    internal void SetSurfaceObscured(bool obscured) => VideoView.Visibility = obscured ? Visibility.Hidden : Visibility.Visible;
    internal void PauseForBackground()
    {
        if (_closing) return;
        _positionTimer.Stop();
        _seekTimer.Stop();
        _pendingScrub = null;
        CancelFrameNavigation();
        if (_mediaPlayer.IsPlaying) _mediaPlayer.SetPause(true);
    }

    internal void MuteForCapture()
    {
        if (_closing) return;
        VolumeSlider.Value = 0;
        _mediaPlayer.Volume = 0;
        _mediaPlayer.Mute = true;
        MuteButton.ToolTip = "Unmute review; system-mix recording can include playback audio";
        UpdateMuteGlyph();
    }

    public void ShowAt(TimeSpan offset)
    {
        if (_closing)
        {
            return;
        }

        Seek(offset, playAfterSeek: true);
        Focus();
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
        if (_started || _closing) return;
        _started = true;
        try
        {
            if (VideoView.Template?.FindName("PART_PlayerHost", VideoView) is HwndHost host)
                _videoBackground = new PlaybackVideoBackground(host);
            Seek(_initialOffset, playAfterSeek: true);
            _positionTimer.Start();
        }
        catch (Exception ex)
        {
            ReportPlaybackFailure(ex);
        }
    }


    private void PlaybackPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_started || _closing) return;
        if (!IsVisible)
        {
            if (_fullscreen) ToggleFullscreen();
            PauseForBackground();
        }
        else if (_mediaPlayer.IsPlaying) _positionTimer.Start();
    }

    public void AppendSession(RecordingSession session)
    {
        if (_closing || session.Id != _session.Id || session.Segments.Count <= _segments.Count) return;
        var ordered = session.Segments.OrderBy(static segment => segment.StartTime).ToArray();
        if (!ordered.Take(_segments.Count).Select(static segment => segment.Id).SequenceEqual(_segments.Select(static segment => segment.Id)))
            return;
        var previousCount = _segments.Count;
        _segmentLease.Add(ordered.Skip(previousCount));
        _session = session;
        _timelineIndex = new PlaybackTimelineIndex(session);
        DurationText.Text = FormatPrecise(session.Duration);
        PlaybackTimeline.Duration = session.Duration;
        PlaybackTimeline.SegmentBoundaries = RecordingTimeline.GetSegmentBoundaries(session);
        RefreshMarkers();
        LiveStatusText.Text = "Time-shift | finalized segments";
        UpdatePositionDisplay(_lastKnownPosition);
        if (_waitingForMore && FollowRecording)
        {
            _waitingForMore = false;
            PlaySegment(previousCount, TimeSpan.Zero, playAfterSeek: true);
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
        if (_mediaPlayer.Volume == 0)
        {
            VolumeSlider.Value = 100;
            _mediaPlayer.Mute = false;
        }
        else _mediaPlayer.Mute = !_mediaPlayer.Mute;
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

    private void PlaybackTimeline_ScrubRequested(object? sender, TimeSpan offset)
    {
        _pendingScrub = offset;
        if (!_seekTimer.IsEnabled) _seekTimer.Start();
    }

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
        if (_markerOperationInFlight || !_allowMarkers)
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
                cancellationToken: _windowCancellation.Token);
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
        if (_waitingForMore)
        {
            _waitingForMore = false;
            LiveStatusText.Text = "Time-shift paused";
            UpdatePlayPauseGlyph();
            return;
        }
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
        var location = _timelineIndex.Locate(current);
        var segmentStart = _segmentStarts[location.SegmentIndex];
        var targetIndex = current - segmentStart > TimeSpan.FromSeconds(1)
            ? location.SegmentIndex
            : Math.Max(0, location.SegmentIndex - 1);
        Seek(_segmentStarts[targetIndex], _mediaPlayer.IsPlaying);
    }

    private void NextSegment()
    {
        var location = _timelineIndex.Locate(CurrentOffset());
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
            var initialPosition = _lastKnownPosition;
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
                    cancellation.Token,
                    _lastKnownPosition - initialPosition == CurrentFrameDuration());
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
            if (!_closing && !_mediaPlayer.IsPlaying) _positionTimer.Stop();
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
        CancellationToken cancellationToken,
        bool nextFrameOnly)
    {
        var restoreVolume = (int)Math.Round(VolumeSlider.Value);
        var restoreMute = _mediaPlayer.Mute;
        _mediaPlayer.Volume = 0;
        try
        {
            _lastKnownPosition = target;
            var location = _timelineIndex.Locate(target);
            if (_continuousInput is null && location.SegmentIndex != _activeSegmentIndex)
            {
                PlaySegment(location.SegmentIndex, location.SegmentOffset, playAfterSeek: false);
                await Task.Delay(100, cancellationToken);
            }
            else
            {
                if (_mediaPlayer.IsPlaying) _mediaPlayer.SetPause(true);
                if (!nextFrameOnly) _mediaPlayer.Time = PlayerTime(target);
                _mediaPlayer.NextFrame();
                await Task.Delay(60, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
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
        SeekDispatchCount++;
        var location = _timelineIndex.Locate(offset);
        if (publishPosition)
        {
            _lastKnownPosition = offset;
        }

        if ((_continuousInput is null && location.SegmentIndex != _activeSegmentIndex) || _activeMedia is null ||
            _mediaPlayer.State is VLCState.Ended or VLCState.Error or VLCState.Stopped)
        {
            PlaySegment(location.SegmentIndex, location.SegmentOffset, playAfterSeek);
        }
        else
        {
            // Clear VLC's frame-step pause before dispatching a playback seek.
            if (playAfterSeek) _mediaPlayer.SetPause(false);
            _mediaPlayer.Time = PlayerTime(offset);
            if (_pendingSegmentTimeMilliseconds is not null)
            {
                _pendingSegmentTimeMilliseconds = PlayerTime(offset);
                _pauseWhenReady = !playAfterSeek;
            }
            _mediaPlayer.SetRate(_playbackRate);
            // Seeking can temporarily report Buffering rather than Paused.
            _mediaPlayer.SetPause(!playAfterSeek);
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
        MediaOpenCount++;
        _waitingForMore = false;
        if (_activeMedia is not null)
        {
            _mediaPlayer.Stop();
            _activeMedia.Dispose();
            SegmentSurfaceExposed?.Invoke();
        }

        if (_continuousInput is not null && segmentIndex >= _continuousSegmentCount)
            ReleaseContinuousInput();

        _activeSegmentIndex = segmentIndex;
        _pendingSegmentTimeMilliseconds = PlayerTime(_segmentStarts[segmentIndex] + segmentOffset);
        _pauseWhenReady = !playAfterSeek;
        if (_continuousInput is not null)
        {
            _continuousStream!.Position = 0;
            _activeMedia = new Media(_libVlc, _continuousInput, ":demux=mkv", ":no-mkv-preload-local-dir");
        }
        else _activeMedia = new Media(_libVlc, new Uri(_segments[segmentIndex].FilePath));
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

            if (IsVisible) _positionTimer.Start();

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

            if (_continuousInput is not null && _continuousSegmentCount < _segments.Count)
            {
                PlaySegment(_continuousSegmentCount, TimeSpan.Zero, playAfterSeek: true);
            }
            else if (_continuousInput is null && _activeSegmentIndex + 1 < _segments.Count)
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
                _waitingForMore = FollowRecording && !_frameStepMode && !_pauseWhenReady;
                _positionTimer.Stop();
                if (_waitingForMore) LiveStatusText.Text = "Waiting for next finalized segment";
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
            (_continuousInput is null ? _segmentStarts[_activeSegmentIndex] : TimeSpan.Zero) +
            TimeSpan.FromMilliseconds(_mediaPlayer.Time));
    }

    private void UpdatePositionDisplay(TimeSpan offset)
    {
        var clamped = offset < TimeSpan.Zero
            ? TimeSpan.Zero
            : offset > _timelineIndex.Duration
                ? _timelineIndex.Duration
                : offset;
        PlaybackTimeline.CursorPosition = clamped;
        PlaybackTimeline.RefreshCursor();
        CurrentTimeText.Text = FormatPrecise(clamped);
        var location = _timelineIndex.Locate(clamped);
        if (_continuousInput is not null) _activeSegmentIndex = location.SegmentIndex;
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
        var maximum = _timelineIndex.Duration > TimeSpan.FromMilliseconds(1)
            ? _timelineIndex.Duration - TimeSpan.FromMilliseconds(1)
            : TimeSpan.Zero;
        return offset < TimeSpan.Zero ? TimeSpan.Zero : offset > maximum ? maximum : offset;
    }

    private void UpdatePlayPauseGlyph()
    {
        if (!IsInitialized || _closing)
        {
            return;
        }

        PlayPauseGlyph.Text = _mediaPlayer.IsPlaying || _waitingForMore ? "\uE769" : "\uE768";
        PlaybackStatusText.Text = _mediaPlayer.IsPlaying
            ? $"Playing at {_playbackRate:0.##}x"
            : _mediaPlayer.State == VLCState.Paused
                ? "Paused"
                : "Ready";
        if (!_mediaPlayer.IsPlaying && !_frameStepPumpRunning) _positionTimer.Stop();
    }

    private void UpdateMuteGlyph()
    {
        if (!IsInitialized || _closing)
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
        var window = Window.GetWindow(this);
        if (window is null) return;
        if (!_fullscreen)
        {
            _restoredWindowStyle = window.WindowStyle;
            _restoredWindowState = window.WindowState;
            _restoredResizeMode = window.ResizeMode;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowState = WindowState.Maximized;
            _fullscreen = true;
        }
        else
        {
            window.WindowStyle = _restoredWindowStyle;
            window.ResizeMode = _restoredResizeMode;
            window.WindowState = _restoredWindowState;
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

    public ValueTask DisposeAsync()
    {
        if (_disposeTask is not null) return new ValueTask(_disposeTask);
        if (_fullscreen) ToggleFullscreen();
        _closing = true;
        _frameStepCancellation?.Cancel();
        _windowCancellation.Cancel();
        _positionTimer.Stop();
        _seekTimer.Stop();
        PlaybackTimeline.ScrubRequested -= PlaybackTimeline_ScrubRequested;
        _mediaPlayer.Playing -= MediaPlayer_Playing;
        _mediaPlayer.Paused -= MediaPlayer_StateChanged;
        _mediaPlayer.Stopped -= MediaPlayer_StateChanged;
        _mediaPlayer.EndReached -= MediaPlayer_EndReached;
        _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;
        _videoBackground?.Dispose();
        _videoBackground = null;
        VideoView.MediaPlayer = null;
        // Disposing the native host alone leaves WPF's parent keyboard site rooted.
        if (VideoView.Template?.FindName("PART_PlayerHost", VideoView) is IKeyboardInputSink sink)
            sink.KeyboardInputSite?.Unregister();
        VideoView.Dispose();
        _disposeTask = _runtime.ReleaseAsync(_mediaPlayer, _activeMedia, _segmentLease, _continuousInput, _continuousStream);
        _continuousInput = null;
        _continuousStream = null;
        _windowCancellation.Dispose();
        return new ValueTask(_disposeTask);
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

    private sealed class PlaybackSegmentLease(ISegmentUsageRegistry registry) : IDisposable
    {
        private readonly List<IDisposable> _leases = [];
        public void Add(IEnumerable<RecordingSegment> segments) => _leases.Add(registry.Acquire(segments.Select(static segment => segment.Id).ToArray()));
        public void Dispose() { foreach (var lease in _leases) lease.Dispose(); }
    }
}
