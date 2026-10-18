using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace Blackbox.App;

public partial class PlaybackPage
{
    internal async Task<object> MeasurePlaybackAsync(int seconds, string directory)
    {
        MuteForCapture();
        var firstFrame = Stopwatch.StartNew();
        await WaitUntilAsync(() => _mediaPlayer.IsPlaying && _activeMedia?.Statistics.DisplayedPictures > 2, "benchmark startup");
        firstFrame.Stop();
        await Task.Delay(3000);
        using var process = Process.GetCurrentProcess();
        var heartbeat = new List<double>();
        var clock = Stopwatch.StartNew();
        var previousTick = clock.Elapsed.TotalMilliseconds;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var now = clock.Elapsed.TotalMilliseconds;
            heartbeat.Add(Math.Max(0, now - previousTick - 100));
            previousTick = now;
        };
        var samples = new List<object>();
        var cpu = process.TotalProcessorTime;
        var start = clock.Elapsed.TotalSeconds;
        var startPosition = _mediaPlayer.Time;
        var displayed = _activeMedia!.Statistics.DisplayedPictures;
        var lost = _activeMedia.Statistics.LostPictures;
        timer.Start();
        try
        {
            for (var second = 0; second < seconds; second++)
            {
                await Task.Delay(1000);
                process.Refresh();
                if (!_mediaPlayer.IsPlaying) throw new InvalidOperationException("Benchmark source ended or playback stalled.");
                samples.Add(new { Second = second + 1, PositionMilliseconds = _mediaPlayer.Time,
                    WorkingSetMiB = process.WorkingSet64 / 1048576d, PrivateMiB = process.PrivateMemorySize64 / 1048576d,
                    Handles = process.HandleCount });
            }
        }
        finally { timer.Stop(); }
        var elapsed = clock.Elapsed.TotalSeconds - start;
        var activeCpu = (process.TotalProcessorTime - cpu).TotalSeconds / elapsed * 100;
        var timelineSeconds = (_mediaPlayer.Time - startPosition) / 1000d;
        var displayedDelta = _activeMedia.Statistics.DisplayedPictures - displayed;
        var lostDelta = _activeMedia.Statistics.LostPictures - lost;
        if (timelineSeconds < elapsed * 0.8 || displayedDelta < 2)
            throw new InvalidOperationException("Playback did not present progressing video.");
        _mediaPlayer.SetPause(true);
        await WaitUntilAsync(() => _mediaPlayer.State == VLCState.Paused, "benchmark pause");
        var seekMilliseconds = new List<double>();
        var frameDiagnostics = new List<object>();
        var reopens = MediaOpenCount;
        for (var index = 0; index < 20; index++)
        {
            var step = Stopwatch.StartNew();
            StepFrame(index % 3 == 0 ? -1 : 1);
            await WaitUntilAsync(() => !_frameStepPumpRunning, "benchmark frame navigation");
            seekMilliseconds.Add(step.Elapsed.TotalMilliseconds);
            frameDiagnostics.Add(new { Index = index, RequestedMilliseconds = _lastKnownPosition.TotalMilliseconds,
                ActualMilliseconds = _mediaPlayer.Time, Pictures = _activeMedia.Statistics.DisplayedPictures, State = _mediaPlayer.State.ToString() });
        }
        if (MediaOpenCount != reopens) throw new InvalidOperationException("Frame benchmark reopened the decoder.");
        var before = await SnapshotHashAsync(directory, "benchmark-before");
        StepFrame(1);
        await WaitUntilAsync(() => !_frameStepPumpRunning, "benchmark frame presentation");
        var after = await SnapshotHashAsync(directory, "benchmark-after");
        var additionalSteps = 1;
        while (before == after && additionalSteps < 5)
        {
            StepFrame(1);
            await WaitUntilAsync(() => !_frameStepPumpRunning, "benchmark duplicate-frame navigation");
            additionalSteps++;
            after = await SnapshotHashAsync(directory, "benchmark-after");
        }
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "frame-navigation-diagnostics.json"),
            System.Text.Json.JsonSerializer.Serialize(frameDiagnostics, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (before == after) throw new InvalidOperationException($"Frame navigation stopped changing pixels across {additionalSteps} steps. Target={_lastKnownPosition}; actual={_mediaPlayer.Time}.");
        await Task.Delay(2000);
        cpu = process.TotalProcessorTime;
        var paused = Stopwatch.StartNew();
        await Task.Delay(5000);
        return new { SampleSeconds = elapsed, ActiveCpuPercentOfOneCore = activeCpu,
            FirstPresentedFramesAfterOpenMilliseconds = firstFrame.Elapsed.TotalMilliseconds,
            TimelineSeconds = timelineSeconds, DisplayedPictures = displayedDelta, LostPictures = lostDelta,
            DisplayedPicturesPerSecond = displayedDelta / elapsed, HardwareDecodingRequested = _mediaPlayer.EnableHardwareDecoding,
            UiHeartbeatDelayP95Milliseconds = Percentile(heartbeat, 0.95), UiHeartbeatDelayMaxMilliseconds = heartbeat.Max(),
            FrameStepMillisecondsMedian = Percentile(seekMilliseconds, 0.5), FrameStepMillisecondsP95 = Percentile(seekMilliseconds, 0.95),
            FrameSteps = 20 + additionalSteps, DecoderReopens = MediaOpenCount - reopens, FramePixelsChanged = true,
            StepsToDifferentPixels = additionalSteps,
            PausedCpuPercentOfOneCore = (process.TotalProcessorTime - cpu).TotalMilliseconds / paused.Elapsed.TotalMilliseconds * 100,
            Samples = samples };
    }

    private static double Percentile(List<double> values, double percentile)
    {
        values.Sort();
        return values[Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1)];
    }
}
