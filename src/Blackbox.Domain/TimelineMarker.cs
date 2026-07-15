namespace Blackbox.Domain;

public sealed record TimelineMarker(
    Guid Id,
    Guid SessionId,
    TimeSpan Offset,
    string Label,
    DateTimeOffset CreatedAt);
