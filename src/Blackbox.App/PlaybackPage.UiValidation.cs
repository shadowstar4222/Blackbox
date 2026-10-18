using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Blackbox.Domain;
using LibVLCSharp.Shared;

namespace Blackbox.App;

public partial class PlaybackPage
{
    internal async Task ValidateFilePreviewAsync()
    {
        MuteForCapture();
        await WaitUntilAsync(() => _mediaPlayer.IsPlaying && _activeMedia?.Statistics.DisplayedPictures > 2, "file preview frames");
        if (VolumeSlider.Value != 0 || (_mediaPlayer.AudioTrackCount > 0 && _mediaPlayer.Volume != 0 && !_mediaPlayer.Mute))
            throw new InvalidOperationException("Capture-safe review did not mute its output.");
        if (MomentsPanel.IsEnabled) throw new InvalidOperationException("Standalone preview must not persist markers into a synthetic session.");
    }

    internal async Task ValidatePlaybackAsync(string directory, RecordingSession extendedSession)
    {
        if (LiveStatusText.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Ordinary review still shows a redundant local-playback label.");
        _mediaPlayer.Mute = true;
        await WaitUntilAsync(() => _mediaPlayer.IsPlaying && _mediaPlayer.Time > 150 &&
            _activeMedia?.Statistics.DisplayedPictures > 2, "first decoded frames");
        if (_mediaPlayer.Hwnd == IntPtr.Zero) throw new InvalidOperationException("Playback is not attached to the in-app video surface.");
        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var sampleStart = Stopwatch.GetTimestamp();
        await Task.Delay(1000);
        var cpuPercentOfOneCore = (process.TotalProcessorTime - cpuStart).TotalMilliseconds /
            Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds * 100;
        var displayedPictures = _activeMedia!.Statistics.DisplayedPictures;
        var lostPictures = _activeMedia.Statistics.LostPictures;
        var snapshot = Path.Combine(directory, "playback-frame.png");
        if (!_mediaPlayer.TakeSnapshot(0, snapshot, 640, 0)) throw new InvalidOperationException("Native video snapshot failed.");
        await WaitUntilAsync(() => File.Exists(snapshot) && new FileInfo(snapshot).Length > 500, "video snapshot");

        _mediaPlayer.SetPause(true);
        await WaitUntilAsync(() => _mediaPlayer.State == VLCState.Paused, "pause");
        Seek(TimeSpan.FromSeconds(0.5), playAfterSeek: false);
        _mediaPlayer.NextFrame();
        await Task.Delay(150);
        var beforeStep = await SnapshotHashAsync(directory, "before-step");
        var beforeStepTime = _mediaPlayer.Time;
        var beforeStepPictures = _activeMedia!.Statistics.DisplayedPictures;
        var frameStepPlayEvents = 0;
        void CountUnexpectedPlay(object? sender, EventArgs e) => Interlocked.Increment(ref frameStepPlayEvents);
        _mediaPlayer.Playing += CountUnexpectedPlay;
        StepFrame(1);
        await WaitUntilAsync(() => !_frameStepPumpRunning, "forward frame");
        await Task.Delay(100);
        var afterStep = await SnapshotHashAsync(directory, "after-step");
        var pixelSteps = 1;
        while (beforeStep == afterStep && pixelSteps < 5)
        {
            StepFrame(1);
            await WaitUntilAsync(() => !_frameStepPumpRunning, "duplicate frame navigation");
            afterStep = await SnapshotHashAsync(directory, "after-step");
            pixelSteps++;
        }
        if (beforeStep == afterStep) throw new InvalidOperationException($"Forward frame navigation did not change pixels across {pixelSteps} steps. Time={beforeStepTime}->{_mediaPlayer.Time}; pictures={beforeStepPictures}->{_activeMedia.Statistics.DisplayedPictures}; target={_lastKnownPosition}; state={_mediaPlayer.State}");
        await Task.Delay(250);
        if (afterStep != await SnapshotHashAsync(directory, "held-step"))
            throw new InvalidOperationException("A paused frame kept moving after frame navigation completed.");
        StepFrame(-1);
        await WaitUntilAsync(() => !_frameStepPumpRunning, "reverse frame presentation");
        var reverseStep = await SnapshotHashAsync(directory, "reverse-step");
        if (reverseStep == afterStep) throw new InvalidOperationException("Reverse frame navigation did not update the displayed frame.");
        var opens = MediaOpenCount;
        for (var index = 0; index < 12; index++)
        {
            StepFrame(index % 2 == 0 ? 1 : -1);
            await WaitUntilAsync(() => !_frameStepPumpRunning, "frame stepping");
        }
        if (MediaOpenCount != opens) throw new InvalidOperationException("Frame stepping reopened the segment decoder.");
        var frameStepReopens = MediaOpenCount - opens;
        _mediaPlayer.Playing -= CountUnexpectedPlay;
        if (frameStepPlayEvents != 0) throw new InvalidOperationException($"Frame navigation briefly played video {frameStepPlayEvents} time(s).");
        if (_mediaPlayer.IsPlaying) throw new InvalidOperationException("Frame stepping left playback running.");
        if (_positionTimer.IsEnabled) throw new InvalidOperationException("Paused frame navigation left its update timer running.");
        if (!_mediaPlayer.TakeSnapshot(0, Path.Combine(directory, "playback-after-frame-seeks.png"), 640, 0))
            throw new InvalidOperationException("Video disappeared after frame stepping.");

        var dispatches = SeekDispatchCount;
        for (var index = 0; index < 100; index++) PlaybackTimeline_ScrubRequested(this, TimeSpan.FromMilliseconds(200 + index));
        await Task.Delay(200);
        if (SeekDispatchCount - dispatches != 1) throw new InvalidOperationException("Rapid scrubbing was not coalesced.");

        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
        var staticBuilds = PlaybackTimeline.StaticLayerBuildCount;
        for (var index = 0; index < 30; index++)
        {
            PlaybackTimeline.CursorPosition = TimeSpan.FromMilliseconds(index * 10);
            PlaybackTimeline.RefreshCursor();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
        }
        if (PlaybackTimeline.StaticLayerBuildCount != staticBuilds)
            throw new InvalidOperationException("Cursor movement rebuilt static timeline graphics.");

        var previousCount = _segments.Count;
        FollowRecording = true;
        if (LiveStatusText.Visibility != Visibility.Visible)
            throw new InvalidOperationException("The relevant time-shift indicator was hidden.");
        Seek(_session.Duration - TimeSpan.FromMilliseconds(200), playAfterSeek: true);
        try { await WaitUntilAsync(() => _waitingForMore, "waiting at the finalized edge"); }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"Follow failed: state={_mediaPlayer.State}, time={_mediaPlayer.Time}, duration={_session.Duration}, frameMode={_frameStepMode}, pending={_pendingSegmentTimeMilliseconds}, pauseReady={_pauseWhenReady}, follow={FollowRecording}", ex);
        }
        AppendSession(extendedSession);
        await WaitUntilAsync(() => _segments.Count > previousCount && _activeSegmentIndex == previousCount && _mediaPlayer.IsPlaying,
            "new finalized segment playback");
        Visibility = Visibility.Collapsed;
        await WaitUntilAsync(() => !_mediaPlayer.IsPlaying, "hidden playback pause");
        if (_positionTimer.IsEnabled || _mediaPlayer.IsPlaying) throw new InvalidOperationException("Hidden review kept playback work active.");
        cpuStart = process.TotalProcessorTime;
        sampleStart = Stopwatch.GetTimestamp();
        await Task.Delay(1000);
        var hiddenCpuPercentOfOneCore = (process.TotalProcessorTime - cpuStart).TotalMilliseconds /
            Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds * 100;
        Visibility = Visibility.Visible;

        File.WriteAllText(Path.Combine(directory, $"playback-results-{(int)ActualWidth}.json"), JsonSerializer.Serialize(new
        {
            AttachedVideoSurface = true, HardwareDecodingRequested = _mediaPlayer.EnableHardwareDecoding,
            StartupCpuPercentOfOneCore = cpuPercentOfOneCore, HiddenCpuPercentOfOneCore = hiddenCpuPercentOfOneCore,
            WorkingSetMiB = process.WorkingSet64 / 1048576d,
            DisplayedPictures = displayedPictures, LostPictures = lostPictures,
            FrameSteps = 13 + pixelSteps, DecoderReopensDuringFrameSteps = frameStepReopens, ForwardFramePixelsChanged = true,
            ReverseFramePixelsChanged = true, PausedFrameStable = true,
            PlayingEventsDuringFrameSteps = frameStepPlayEvents,
            StepsToDifferentPixels = pixelSteps,
            ScrubRequests = 100, ScrubDispatches = 1, CursorUpdates = 30, StaticLayerRebuilds = 0,
            FinalizedSegmentFollow = true, HiddenPlaybackPaused = true
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<string> SnapshotHashAsync(string directory, string label)
    {
        var path = Path.Combine(directory, $"playback-{label}-{(int)ActualWidth}-{Guid.NewGuid():N}.png");
        if (!_mediaPlayer.TakeSnapshot(0, path, 640, 0)) throw new InvalidOperationException("Frame snapshot failed.");
        await WaitUntilAsync(() => File.Exists(path) && new FileInfo(path).Length > 500, "frame snapshot publication");
        await Task.Delay(50);
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string operation)
    {
        var start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(12)) throw new TimeoutException($"Playback check timed out: {operation}.");
            await Task.Delay(25);
        }
    }
}
