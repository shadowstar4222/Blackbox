namespace Blackbox.Domain;

public sealed record MicrophoneProcessingSettings
{
    public bool NoiseSuppressionEnabled { get; init; } = true;
    public bool ExpanderEnabled { get; init; } = true;
    public double ExpanderThresholdDb { get; init; } = -45;
    public bool CompressorEnabled { get; init; } = true;
    public double CompressorThresholdDb { get; init; } = -18;
    public double CompressorRatio { get; init; } = 3;
    public bool LimiterEnabled { get; init; } = true;
    public double LimiterThresholdDb { get; init; } = -3;

    public void Validate()
    {
        if (ExpanderThresholdDb is < -80 or > 0)
        {
            throw new InvalidOperationException("Expander threshold must be between -80 dB and 0 dB.");
        }

        if (CompressorThresholdDb is < -60 or > 0)
        {
            throw new InvalidOperationException("Compressor threshold must be between -60 dB and 0 dB.");
        }

        if (CompressorRatio is < 1 or > 20)
        {
            throw new InvalidOperationException("Compressor ratio must be between 1:1 and 20:1.");
        }

        if (LimiterThresholdDb is < -20 or > 0)
        {
            throw new InvalidOperationException("Limiter threshold must be between -20 dB and 0 dB.");
        }
    }
}
