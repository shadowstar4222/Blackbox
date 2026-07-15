using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Storage;

public sealed class StorageQuotaEnforcer(
    ISegmentRepository repository,
    IClock clock,
    ILogger<StorageQuotaEnforcer> logger)
{
    public async Task<StoragePruneResult> EnforceAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        await repository.ReconcileMissingFilesAsync(cancellationToken);

        var segments = await repository.GetAllAsync(cancellationToken);
        var retainedBytes = segments.Sum(static segment => segment.FileSizeBytes);
        var retainedDuration = CalculateRetainedDuration(segments);
        var maximumBytes = GigabytesToBytes(settings.MaximumStorageGigabytes);
        var minimumFreeBytes = GigabytesToBytes(settings.MinimumRemainingFreeDiskSpaceGigabytes);
        var oldestAllowedStart = clock.UtcNow - settings.MaximumRetainedDuration;
        var freeBytes = GetAvailableFreeBytes(settings.RecordingLocation);
        var deletedSegments = 0;
        long deletedBytes = 0;

        foreach (var segment in segments.OrderBy(static segment => segment.StartTime))
        {
            if (!ShouldDelete(segment, retainedBytes, maximumBytes, oldestAllowedStart, freeBytes, minimumFreeBytes))
            {
                continue;
            }

            if (segment.IsProtected)
            {
                continue;
            }

            if (!TryDeleteSegmentFile(segment))
            {
                continue;
            }

            await repository.DeleteByIdAsync(segment.Id, cancellationToken);
            retainedBytes -= segment.FileSizeBytes;
            freeBytes += segment.FileSizeBytes;
            deletedBytes += segment.FileSizeBytes;
            deletedSegments++;
            logger.LogInformation(
                "Deleted unprotected segment {SegmentId} at {FilePath} to enforce storage policy.",
                segment.Id,
                segment.FilePath);
        }

        var remaining = await repository.GetAllAsync(cancellationToken);
        return new StoragePruneResult(deletedSegments, deletedBytes, retainedBytes, CalculateRetainedDuration(remaining));
    }

    private static bool ShouldDelete(
        RecordingSegment segment,
        long retainedBytes,
        long maximumBytes,
        DateTimeOffset oldestAllowedStart,
        long freeBytes,
        long minimumFreeBytes)
    {
        return retainedBytes > maximumBytes || segment.StartTime < oldestAllowedStart || freeBytes < minimumFreeBytes;
    }

    private static TimeSpan CalculateRetainedDuration(IReadOnlyList<RecordingSegment> segments)
    {
        if (segments.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var start = segments.Min(static segment => segment.StartTime);
        var end = segments.Max(static segment => segment.EndTime);
        return end - start;
    }

    private static long GigabytesToBytes(decimal gigabytes)
    {
        return (long)(gigabytes * 1024 * 1024 * 1024);
    }

    private static long GetAvailableFreeBytes(string recordingLocation)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(recordingLocation));
        if (string.IsNullOrWhiteSpace(root))
        {
            return long.MaxValue;
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static bool TryDeleteSegmentFile(RecordingSegment segment)
    {
        if (!File.Exists(segment.FilePath))
        {
            return true;
        }

        try
        {
            using (File.Open(segment.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            File.Delete(segment.FilePath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
