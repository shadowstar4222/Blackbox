using Blackbox.Domain;

namespace Blackbox.Export;

internal sealed record RecordingFileState(string Path, long Length, DateTime LastWriteTimeUtc)
{
    public static RecordingFileState? Capture(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length > 0
                ? new RecordingFileState(file.FullName, file.Length, file.LastWriteTimeUtc)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static FileStream? TryOpenReadLease(string path)
    {
        try
        {
            // Deny writers while probing, but permit atomic recovery publication.
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IEnumerable<string> Enumerate(string root, bool includeActive = false)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!RecordingDirectoryLayout.IsInternalPath(root, System.IO.Path.Combine(child, "_")) &&
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                {
                    directories.Push(child);
                }
            }

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var name = System.IO.Path.GetFileName(path);
                var extension = System.IO.Path.GetExtension(path);
                if ((!extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) &&
                     !extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)) ||
                    name.Contains(".partial", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!name.Contains(".active", StringComparison.OrdinalIgnoreCase) ||
                    includeActive && NativeRecordingFile.TryParse(path) is { IsActive: true })
                {
                    yield return path;
                }
            }
        }
    }
}
