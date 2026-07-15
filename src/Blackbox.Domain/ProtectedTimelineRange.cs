namespace Blackbox.Domain;

public sealed record ProtectedTimelineRange(
    Guid Id,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    DateTimeOffset CreatedAt);
