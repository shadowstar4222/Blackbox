namespace Blackbox.Infrastructure;

public sealed record GpuActivityOptions
{
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMilliseconds(150);
    public double ActiveThresholdPercent { get; init; } = 1;

    public void Validate()
    {
        if (SampleInterval <= TimeSpan.Zero ||
            ActiveThresholdPercent is < 0 or > 100)
        {
            throw new InvalidOperationException("GPU activity sampling settings are invalid.");
        }
    }
}
