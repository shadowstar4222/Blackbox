using System.IO;
using Blackbox.Export;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

public partial class PlaybackPage
{
    internal async Task PrepareContinuousInputAsync()
    {
        if (FollowRecording || _segments.Count < 2 ||
            _segments.Any(static segment => !Path.GetExtension(segment.FilePath).Equals(".mkv", StringComparison.OrdinalIgnoreCase)))
            return;
        try
        {
            var preparation = System.Diagnostics.Stopwatch.StartNew();
            var cancellation = _windowCancellation.Token;
            var segments = _segments;
            var stream = await Task.Run(() => MatroskaPlaybackStream.Create(segments, cancellation), cancellation);
            if (_closing) { stream.Dispose(); return; }
            _continuousStream = stream;
            _continuousInput = new StreamMediaInput(stream);
            _continuousSegmentCount = _segments.Count;
            _logger.LogInformation("Continuous review indexed {SegmentCount} segments in {ElapsedMilliseconds:0.0} ms; metadata read {IndexedBytes} bytes.",
                _segments.Count, preparation.Elapsed.TotalMilliseconds, stream.IndexedBytesRead);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (NotSupportedException exception)
        {
            _logger.LogWarning(exception, "Recording layout requires per-segment playback.");
        }
    }

    private long PlayerTime(TimeSpan offset)
    {
        var time = _continuousInput is not null ? offset : _timelineIndex.Locate(offset).SegmentOffset;
        return Math.Max(0, (long)time.TotalMilliseconds);
    }

    private void ReleaseContinuousInput()
    {
        _continuousInput?.Dispose();
        _continuousStream?.Dispose();
        _continuousInput = null;
        _continuousStream = null;
    }
}
