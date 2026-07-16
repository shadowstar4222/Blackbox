namespace Blackbox.Recording;

public sealed record AutomaticCaptureOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public int RequiredPositiveDetections { get; init; } = 2;
    public TimeSpan StopGracePeriod { get; init; } = TimeSpan.FromSeconds(15);

    public void Validate()
    {
        if (PollInterval <= TimeSpan.Zero || RequiredPositiveDetections <= 0 || StopGracePeriod < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Automatic capture timing settings are invalid.");
        }
    }
}
