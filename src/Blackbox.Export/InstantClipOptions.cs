namespace Blackbox.Export;

public sealed record InstantClipOptions
{
    public required string OutputDirectory { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan LibraryRefreshInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PendingTimeout { get; init; } = TimeSpan.FromMinutes(20);
    public TimeSpan EndTimeTolerance { get; init; } = TimeSpan.FromSeconds(2);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory) ||
            PollInterval <= TimeSpan.Zero ||
            LibraryRefreshInterval <= TimeSpan.Zero ||
            PendingTimeout <= TimeSpan.Zero ||
            EndTimeTolerance < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Instant clip options are invalid.");
        }
    }
}
