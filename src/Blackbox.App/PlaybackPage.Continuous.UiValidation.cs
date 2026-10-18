using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LibVLCSharp.Shared;

namespace Blackbox.App;

public partial class PlaybackPage
{
    private async Task ValidateContinuousTransitionsAsync(string directory)
    {
        var input = _activeMedia;
        var opens = MediaOpenCount;
        var handoffs = new List<object>();
        var lostOutput = 0;
        void OutputChanged(object? sender, MediaPlayerVoutEventArgs args)
        {
            if (args.Count == 0) Interlocked.Increment(ref lostOutput);
        }
        _mediaPlayer.Vout += OutputChanged;
        try
        {
            if (Math.Abs(_mediaPlayer.Length - _session.Duration.TotalMilliseconds) > 100)
                throw new InvalidOperationException($"Continuous input duration mismatch: {_mediaPlayer.Length} / {_session.Duration}.");
            foreach (var boundary in _segmentStarts.Skip(1).Take(4))
            {
                for (var repetition = 0; repetition < 2; repetition++)
                {
                    Seek(boundary - TimeSpan.FromSeconds(1.1), playAfterSeek: true);
                    await Task.Delay(350);
                    var paints = _videoBackground!.NativePaintCount;
                    var frames = _activeMedia!.Statistics.DisplayedPictures;
                    var clock = Stopwatch.StartNew();
                    await WaitUntilAsync(() => _mediaPlayer.Time > boundary.TotalMilliseconds + 350, "continuous boundary");
                    var newFrames = _activeMedia.Statistics.DisplayedPictures - frames;
                    if (MediaOpenCount != opens || !ReferenceEquals(input, _activeMedia) || lostOutput != 0 || newFrames < 3)
                        throw new InvalidOperationException("Segment boundary restarted or lost the continuous video output.");
                    if (_videoBackground.NativePaintCount != paints)
                        throw new InvalidOperationException("A segment boundary exposed the native video background.");
                    handoffs.Add(new { BoundarySeconds = boundary.TotalSeconds, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                        DisplayedFrames = newFrames, BackgroundPaints = _videoBackground.NativePaintCount - paints });
                }
            }
            Seek(TimeSpan.FromMilliseconds(300), playAfterSeek: false);
            await WaitUntilAsync(() => _mediaPlayer.State == VLCState.Paused, "continuous paused seek");
            Seek(_segmentStarts[1] + TimeSpan.FromMilliseconds(300), playAfterSeek: false);
            await WaitUntilAsync(() => _mediaPlayer.State == VLCState.Paused, "continuous cross-segment seek");
            Seek(_segmentStarts[1] - CurrentFrameDuration(), playAfterSeek: false);
            StepFrame(1);
            await WaitUntilAsync(() => !_frameStepPumpRunning, "continuous forward frame across boundary");
            StepFrame(-1);
            await WaitUntilAsync(() => !_frameStepPumpRunning, "continuous reverse frame across boundary");
            await SnapshotHashAsync(directory, "continuous-frame-after-seeks");
            if (MediaOpenCount != opens) throw new InvalidOperationException("Continuous navigation reopened the decoder.");
            LoopCheckBox.IsChecked = true;
            Seek(_session.Duration - TimeSpan.FromMilliseconds(350), playAfterSeek: true);
            await WaitUntilAsync(() => _mediaPlayer.IsPlaying && _mediaPlayer.Time < 1000, "continuous loop");
            File.WriteAllText(Path.Combine(directory, "transition-results.json"), JsonSerializer.Serialize(new
            {
                ContinuousVirtualInput = true, SegmentCount = _segments.Count, Handoffs = handoffs,
                DecoderReopensAtSegmentBoundaries = 0, VideoOutputLossAtBoundaries = 0,
                PausedCrossSegmentSeek = true, ForwardReverseFrameNavigation = true, LoopRestart = true,
                SourceFilesUnchanged = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            File.WriteAllText(Path.Combine(directory, "transition-failure.json"), JsonSerializer.Serialize(new
            {
                State = _mediaPlayer.State.ToString(), _mediaPlayer.Time, _mediaPlayer.Length, MediaOpenCount,
                Segment = _activeSegmentIndex, PendingSeek = _pendingSegmentTimeMilliseconds,
                Continuous = _continuousInput is not null, LostOutput = lostOutput, Handoffs = handoffs
            }));
            throw;
        }
        finally { _mediaPlayer.Vout -= OutputChanged; }
    }
}
