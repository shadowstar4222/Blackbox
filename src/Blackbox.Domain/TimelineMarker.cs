namespace Blackbox.Domain;

public enum TimelineMarkerOrigin
{
    Manual,
    Live,
    AudioSpike,
    SceneChange,
    InputRule
}

public sealed record TimelineMarker(
    Guid Id,
    Guid SessionId,
    TimeSpan Offset,
    string Label,
    DateTimeOffset CreatedAt,
    TimeSpan ContextBefore = default,
    TimeSpan ContextAfter = default,
    TimelineMarkerOrigin Origin = TimelineMarkerOrigin.Manual,
    string? Reason = null)
{
    public void Validate()
    {
        if (Id == Guid.Empty ||
            Offset < TimeSpan.Zero ||
            ContextBefore < TimeSpan.Zero ||
            ContextAfter < TimeSpan.Zero ||
            ContextBefore > TimeSpan.FromMinutes(30) ||
            ContextAfter > TimeSpan.FromMinutes(30) ||
            string.IsNullOrWhiteSpace(Label) ||
            !Enum.IsDefined(Origin))
        {
            throw new InvalidOperationException("The timeline marker is invalid.");
        }
    }
}
