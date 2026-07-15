namespace Blackbox.Domain;

public sealed record StoragePruneResult(
    int DeletedSegments,
    long DeletedBytes,
    long CurrentRetainedBytes,
    TimeSpan CurrentRetainedDuration);
