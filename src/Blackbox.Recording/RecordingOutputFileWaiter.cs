using System.Diagnostics;

namespace Blackbox.Recording;

internal static class RecordingOutputFileWaiter
{
    public static async Task<string> WaitAsync(
        string? outputPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new InvalidOperationException("OBS did not return a recording output path.");
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var output = new FileInfo(outputPath);
                if (output.Exists && output.Length > 0)
                {
                    return outputPath;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new InvalidOperationException("OBS did not finalize the expected recording file.");
    }
}
