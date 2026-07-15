namespace Blackbox.Domain;

public sealed record SessionExportResult(
    string OutputPath,
    TimeSpan Duration,
    long FileSizeBytes,
    bool UsedStreamCopy);
