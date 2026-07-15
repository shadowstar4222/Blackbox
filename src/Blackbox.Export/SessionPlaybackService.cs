using System.Diagnostics;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Export;

public sealed class SessionPlaybackService(
    IFfmpegProvisioner ffmpegProvisioner,
    ISegmentUsageRegistry usageRegistry,
    FfmpegOptions options,
    ILogger<SessionPlaybackService> logger)
{
    public async Task PlayAsync(
        RecordingSession session,
        IProgress<RecordingLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (session.HasGaps || session.HasMissingSegments)
        {
            throw new InvalidOperationException("This recording has a missing section and cannot play continuously.");
        }

        var provisionProgress = new Progress<FfmpegProvisionProgress>(update =>
            progress?.Report(new RecordingLibraryProgress(update.Message, update.Percent)));
        var installation = await ffmpegProvisioner.EnsureInstalledAsync(provisionProgress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var lease = usageRegistry.Acquire(session.Segments.Select(static segment => segment.Id).ToArray());
        var concatPath = FfmpegConcatFile.Create(options.WorkDirectory, session.Segments);
        var startInfo = new ProcessStartInfo
        {
            FileName = installation.FfplayPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-autoexit",
            "-window_title", $"Blackbox - {session.StartTime.LocalDateTime:g}",
            "-f", "concat",
            "-safe", "0",
            "-i", concatPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows could not start the Blackbox player.");
            progress?.Report(new RecordingLibraryProgress("Playing the continuous recording.", 100));
            _ = MonitorPlaybackAsync(process, lease, concatPath);
        }
        catch
        {
            process?.Dispose();
            lease.Dispose();
            TryDeleteFile(concatPath);
            throw;
        }
    }

    private async Task MonitorPlaybackAsync(Process process, IDisposable lease, string concatPath)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Could not monitor the Blackbox playback process.");
        }
        finally
        {
            process.Dispose();
            lease.Dispose();
            TryDeleteFile(concatPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
