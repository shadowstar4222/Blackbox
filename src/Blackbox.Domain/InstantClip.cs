namespace Blackbox.Domain;

public enum InstantClipStatus
{
    Pending,
    Ready,
    Protected,
    Exported,
    Failed
}

public sealed record InstantClip
{
    public required Guid Id { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required DateTimeOffset RequestedStartTime { get; init; }
    public required DateTimeOffset RequestedEndTime { get; init; }
    public required string Label { get; init; }
    public required InstantClipStatus Status { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public Guid? SessionId { get; init; }
    public DateTimeOffset? ActualStartTime { get; init; }
    public DateTimeOffset? ActualEndTime { get; init; }
    public string? GameTitle { get; init; }
    public string? OutputPath { get; init; }
    public string? FailureDetail { get; init; }
    public IReadOnlyList<Guid> SourceSegmentIds { get; init; } = [];

    public TimeSpan RequestedDuration => RequestedEndTime - RequestedStartTime;
    public TimeSpan? ActualDuration => ActualStartTime is not null && ActualEndTime is not null
        ? ActualEndTime.Value - ActualStartTime.Value
        : null;
    public bool RetainsSourceSegments => Status is InstantClipStatus.Pending or InstantClipStatus.Failed;
    public bool IsPlayable =>
        Status is InstantClipStatus.Ready or InstantClipStatus.Protected or InstantClipStatus.Exported &&
        !string.IsNullOrWhiteSpace(OutputPath);

    public void Validate()
    {
        if (Id == Guid.Empty ||
            RequestedEndTime <= RequestedStartTime ||
            RequestedDuration > TimeSpan.FromMinutes(30) ||
            string.IsNullOrWhiteSpace(Label) ||
            !Enum.IsDefined(Status))
        {
            throw new InvalidOperationException("The instant clip request is invalid.");
        }

        if ((ActualStartTime is null) != (ActualEndTime is null) ||
            ActualEndTime <= ActualStartTime)
        {
            throw new InvalidOperationException("The instant clip's actual range is invalid.");
        }

        if (SourceSegmentIds is null ||
            SourceSegmentIds.Any(static id => id == Guid.Empty) ||
            SourceSegmentIds.Distinct().Count() != SourceSegmentIds.Count)
        {
            throw new InvalidOperationException("Instant clip source segments must be unique.");
        }

        if (Status is InstantClipStatus.Ready or InstantClipStatus.Protected or InstantClipStatus.Exported &&
            (SessionId is null ||
             ActualStartTime is null ||
             ActualEndTime is null ||
             string.IsNullOrWhiteSpace(OutputPath)))
        {
            throw new InvalidOperationException("A finished instant clip requires its session, range, and output.");
        }

        if (Status == InstantClipStatus.Failed && string.IsNullOrWhiteSpace(FailureDetail))
        {
            throw new InvalidOperationException("A failed instant clip requires a failure detail.");
        }
    }
}
