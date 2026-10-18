namespace Blackbox.Recording;

public sealed record TimelineMarkerOptions
{
    public TimeSpan ContextBefore { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ContextAfter { get; init; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (ContextBefore < TimeSpan.Zero ||
            ContextAfter < TimeSpan.Zero ||
            ContextBefore > TimeSpan.FromMinutes(30) ||
            ContextAfter > TimeSpan.FromMinutes(30))
        {
            throw new InvalidOperationException("Timeline marker context is invalid.");
        }
    }
}
