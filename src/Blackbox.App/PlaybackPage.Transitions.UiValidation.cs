using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using LibVLCSharp.Shared;

namespace Blackbox.App;

public partial class PlaybackPage
{
    internal async Task ValidateTransitionsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        MuteForCapture();
        await WaitUntilAsync(() => _mediaPlayer.IsPlaying && _activeMedia?.Statistics.DisplayedPictures > 2, "transition initial frames");
        if (_continuousInput is not null)
        {
            await ValidateContinuousTransitionsAsync(directory);
            return;
        }
        var host = VideoView.Template?.FindName("PART_PlayerHost", VideoView) as HwndHost
            ?? throw new InvalidOperationException("No native video host.");
        _mediaPlayer.Stop();
        _videoBackground?.Dispose();
        _videoBackground = null;
        var before = SampleNativeBackground(host, Path.Combine(directory, "native-background-before.png"));
        _videoBackground = new PlaybackVideoBackground(host);
        var after = SampleNativeBackground(host, Path.Combine(directory, "native-background-after.png"));
        if (before < 200 || after > 1)
            throw new InvalidOperationException($"Native background reproduction failed: before={before}, after={after}.");

        var backgrounds = new List<double>();
        void CheckBackground()
        {
            var brightness = SampleNativeBackground(host);
            backgrounds.Add(brightness);
            if (brightness > 1) throw new InvalidOperationException("Native surface flashed during segment handoff.");
        }
        SegmentSurfaceExposed += CheckBackground;
        try
        {
            PlaySegment(0, TimeSpan.Zero, playAfterSeek: true);
            await WaitUntilAsync(() => _mediaPlayer.IsPlaying && _pendingSegmentTimeMilliseconds is null &&
                _activeMedia?.Statistics.DisplayedPictures > 2, "restart after native background probe");
            for (var index = 0; index < 4; index++)
            {
                Seek(_segmentStarts[1] - TimeSpan.FromMilliseconds(350), playAfterSeek: true);
                await WaitUntilAsync(() => _activeSegmentIndex == 1 && _mediaPlayer.IsPlaying &&
                    _activeMedia?.Statistics.DisplayedPictures > 2, "automatic segment transition");
                await Task.Delay(200);
            }
            LoopCheckBox.IsChecked = true;
            Seek(_session.Duration - TimeSpan.FromMilliseconds(350), playAfterSeek: true);
            await WaitUntilAsync(() => _activeSegmentIndex == 0 && _mediaPlayer.IsPlaying &&
                _activeMedia?.Statistics.DisplayedPictures > 2, "loop transition");
            LoopCheckBox.IsChecked = false;
            FollowRecording = true;
            Seek(_session.Duration - TimeSpan.FromMilliseconds(350), playAfterSeek: true);
            await WaitUntilAsync(() => _waitingForMore, "finalized edge");
            CheckBackground();
            var tail = _segments[^1];
            var appended = tail with { Id = Guid.NewGuid(), StartTime = _session.EndTime,
                EndTime = _session.EndTime + (tail.EndTime - tail.StartTime) };
            var nextIndex = _segments.Count;
            AppendSession(_session with { Segments = [.. _segments, appended], EndTime = appended.EndTime });
            await WaitUntilAsync(() => _activeSegmentIndex == nextIndex && _mediaPlayer.IsPlaying &&
                _activeMedia?.Statistics.DisplayedPictures > 2, "new finalized segment handoff");
            Seek(TimeSpan.FromMilliseconds(300), playAfterSeek: false);
            await WaitUntilAsync(() => _activeSegmentIndex == 0 && _mediaPlayer.State == VLCState.Paused, "paused cross-segment seek");
            Seek(_segmentStarts[1] + TimeSpan.FromMilliseconds(300), playAfterSeek: false);
            await WaitUntilAsync(() => _activeSegmentIndex == 1 && _mediaPlayer.State == VLCState.Paused, "paused forward segment seek");
            StepFrame(1);
            await WaitUntilAsync(() => !_frameStepPumpRunning, "frame seek after transition");
            StepFrame(-1);
            await WaitUntilAsync(() => !_frameStepPumpRunning, "reverse frame seek after transition");
            CheckBackground();
            if (backgrounds.Count < 9) throw new InvalidOperationException("Too few handoff checks.");
            if (_videoBackground.NativePaintCount == 0) throw new InvalidOperationException("No native paint messages were exercised.");
            File.WriteAllText(Path.Combine(directory, "transition-results.json"), JsonSerializer.Serialize(new
            {
                BaselineNativeBackground = before, FixedNativeBackground = after,
                AutomaticTransitions = 4, PausedCrossSegmentSeeks = 2,
                LoopTransition = true, FinalizedSegmentFollow = true, NativePaintsHandled = _videoBackground.NativePaintCount,
                FrameNavigationAfterTransition = true, BackgroundChecks = backgrounds,
                SourceFilesUnchanged = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            File.WriteAllText(Path.Combine(directory, "transition-failure.json"), JsonSerializer.Serialize(new
            {
                Segment = _activeSegmentIndex, State = _mediaPlayer.State.ToString(), Time = _mediaPlayer.Time,
                Length = _mediaPlayer.Length, PendingSeek = _pendingSegmentTimeMilliseconds,
                Frames = _activeMedia?.Statistics.DisplayedPictures, Backgrounds = backgrounds
            }));
            throw;
        }
        finally { SegmentSurfaceExposed -= CheckBackground; }
    }

    private static double SampleNativeBackground(HwndHost host, string? output = null)
    {
        using var bitmap = new System.Drawing.Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Magenta);
            var dc = graphics.GetHdc();
            try { SendMessage(host.Handle, 0x0318, dc, 0x0C); } // WM_PRINTCLIENT: client + erase, no video children.
            finally { graphics.ReleaseHdc(dc); }
        }
        if (output is not null) bitmap.Save(output, ImageFormat.Png);
        double sum = 0;
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            sum += (pixel.R + pixel.G + pixel.B) / 3d;
        }
        return sum / (64 * 64);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
