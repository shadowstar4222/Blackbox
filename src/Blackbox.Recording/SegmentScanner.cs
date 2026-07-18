using Blackbox.Domain;

namespace Blackbox.Recording;

public sealed class SegmentScanner(ISegmentRepository repository)
{
    public async Task<int> ImportCompletedSegmentsAsync(
        string recordingDirectory,
        SegmentMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(recordingDirectory))
        {
            return 0;
        }

        var imported = 0;
        foreach (var file in Directory
                     .EnumerateFiles(recordingDirectory, "*.mkv", SearchOption.AllDirectories)
                     .Where(path => !RecordingDirectoryLayout.IsInternalPath(recordingDirectory, path))
                     .OrderBy(static path => path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.EndsWith(".active.mkv", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(file);
            if (await repository.ExistsByPathAsync(fullPath, cancellationToken))
            {
                continue;
            }

            var info = new FileInfo(fullPath);
            if (!IsStable(info))
            {
                continue;
            }

            await repository.UpsertAsync(new RecordingSegment(
                Guid.NewGuid(),
                metadata.SessionId,
                metadata.StartTime,
                metadata.EndTime,
                metadata.GameExecutable,
                metadata.GameTitle,
                metadata.VideoFormat,
                metadata.AudioTrackLayout,
                metadata.Encoder,
                metadata.Width,
                metadata.Height,
                metadata.FrameRate,
                metadata.IsHdr,
                false,
                fullPath,
                info.Length), cancellationToken);
            imported++;
        }

        return imported;
    }

    private static bool IsStable(FileInfo info)
    {
        info.Refresh();
        return info.Exists && info.Length > 0 && DateTimeOffset.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromSeconds(1);
    }
}
