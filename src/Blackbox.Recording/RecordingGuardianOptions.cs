namespace Blackbox.Recording;

public sealed record RecordingGuardianOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan RepairCooldown { get; init; } = TimeSpan.FromSeconds(10);
    public int BlackFrameConfirmationSamples { get; init; } = 2;
    public int FrozenFrameConfirmationSamples { get; init; } = 10;
    public int SilentAudioConfirmationSamples { get; init; } = 10;
    public int MissingPreviewConfirmationSamples { get; init; } = 3;
    public int ObsUnavailableConfirmationSamples { get; init; } = 2;
    public int HealthyConfirmationSamples { get; init; } = 2;
    public int MaximumRepairAttempts { get; init; } = 2;
    public double BlackMeanLuminanceThreshold { get; init; } = 0.025;
    public double BlackDarkPixelRatioThreshold { get; init; } = 0.985;
    public double SilentAudioPeakDbThreshold { get; init; } = -55;

    public void Validate()
    {
        if (PollInterval <= TimeSpan.Zero ||
            ProbeTimeout <= TimeSpan.Zero ||
            RepairCooldown < TimeSpan.Zero ||
            BlackFrameConfirmationSamples < 1 ||
            FrozenFrameConfirmationSamples < 2 ||
            SilentAudioConfirmationSamples < 2 ||
            MissingPreviewConfirmationSamples < 1 ||
            ObsUnavailableConfirmationSamples < 1 ||
            HealthyConfirmationSamples < 1 ||
            MaximumRepairAttempts < 1 ||
            BlackMeanLuminanceThreshold is < 0 or > 1 ||
            BlackDarkPixelRatioThreshold is < 0 or > 1 ||
            SilentAudioPeakDbThreshold > 0)
        {
            throw new InvalidOperationException("Recording Guardian options contain an invalid threshold or interval.");
        }
    }
}
