namespace Blackbox.Infrastructure;

public sealed record SupportBundleResult(
    string FilePath,
    long FileSizeBytes,
    int IncludedLogEntries,
    int RedactionCount);
