namespace Blackbox.Domain;

public enum MicrophoneVoicePreset
{
    Natural,
    Broadcast,
    StrongNoiseRemoval,
    Custom
}

public sealed record MicrophoneProcessingSettings
{
    public MicrophoneVoicePreset Preset { get; init; } = MicrophoneVoicePreset.Broadcast;
    public bool ProcessingEnabled { get; init; } = true;
    public double InputGainDb { get; init; }
    public bool HighPassEnabled { get; init; } = true;
    public double HighPassCutoffHz { get; init; } = 80;
    public bool NoiseSuppressionEnabled { get; init; } = true;
    public double NoiseSuppressionDb { get; init; } = -24;
    public bool ExpanderEnabled { get; init; } = true;
    public double ExpanderThresholdDb { get; init; } = -45;
    public double ExpanderRatio { get; init; } = 2.5;
    public double LowMidEqDb { get; init; } = -2;
    public double PresenceEqDb { get; init; } = 2;
    public bool CompressorEnabled { get; init; } = true;
    public double CompressorThresholdDb { get; init; } = -18;
    public double CompressorRatio { get; init; } = 3;
    public double CompressorMakeupGainDb { get; init; } = 2;
    public bool DeEsserEnabled { get; init; } = true;
    public double DeEsserThresholdDb { get; init; } = -20;
    public bool LimiterEnabled { get; init; } = true;
    public double LimiterThresholdDb { get; init; } = -1;

    public static MicrophoneProcessingSettings ForPreset(
        MicrophoneVoicePreset preset,
        double inputGainDb = 0) => preset switch
        {
            MicrophoneVoicePreset.Natural => new()
            {
                Preset = preset,
                InputGainDb = inputGainDb,
                HighPassCutoffHz = 70,
                NoiseSuppressionDb = -16,
                ExpanderThresholdDb = -48,
                ExpanderRatio = 1.8,
                LowMidEqDb = -1,
                PresenceEqDb = 1,
                CompressorThresholdDb = -16,
                CompressorRatio = 2,
                CompressorMakeupGainDb = 1,
                DeEsserThresholdDb = -16,
                LimiterThresholdDb = -1
            },
            MicrophoneVoicePreset.Broadcast => new()
            {
                Preset = preset,
                InputGainDb = inputGainDb
            },
            MicrophoneVoicePreset.StrongNoiseRemoval => new()
            {
                Preset = preset,
                InputGainDb = inputGainDb,
                HighPassCutoffHz = 90,
                NoiseSuppressionDb = -32,
                ExpanderThresholdDb = -40,
                ExpanderRatio = 4,
                LowMidEqDb = -3,
                PresenceEqDb = 2.5,
                CompressorThresholdDb = -20,
                CompressorRatio = 3.5,
                CompressorMakeupGainDb = 2,
                DeEsserThresholdDb = -22,
                LimiterThresholdDb = -1
            },
            MicrophoneVoicePreset.Custom => new()
            {
                Preset = preset,
                InputGainDb = inputGainDb
            },
            _ => throw new ArgumentOutOfRangeException(nameof(preset))
        };

    public void Validate()
    {
        if (!Enum.IsDefined(Preset))
        {
            throw new InvalidOperationException("Microphone voice preset is invalid.");
        }

        if (!double.IsFinite(InputGainDb) ||
            !double.IsFinite(HighPassCutoffHz) ||
            !double.IsFinite(NoiseSuppressionDb) ||
            !double.IsFinite(ExpanderThresholdDb) ||
            !double.IsFinite(ExpanderRatio) ||
            !double.IsFinite(LowMidEqDb) ||
            !double.IsFinite(PresenceEqDb) ||
            !double.IsFinite(CompressorThresholdDb) ||
            !double.IsFinite(CompressorRatio) ||
            !double.IsFinite(CompressorMakeupGainDb) ||
            !double.IsFinite(DeEsserThresholdDb) ||
            !double.IsFinite(LimiterThresholdDb))
        {
            throw new InvalidOperationException("Microphone processing values must be finite numbers.");
        }

        if (InputGainDb is < -30 or > 30)
        {
            throw new InvalidOperationException("Input gain must be between -30 dB and 30 dB.");
        }

        if (HighPassCutoffHz is < 40 or > 180)
        {
            throw new InvalidOperationException("High-pass cutoff must be between 40 Hz and 180 Hz.");
        }

        if (NoiseSuppressionDb is < -60 or > 0)
        {
            throw new InvalidOperationException("Noise suppression must be between -60 dB and 0 dB.");
        }

        if (ExpanderThresholdDb is < -80 or > 0)
        {
            throw new InvalidOperationException("Expander threshold must be between -80 dB and 0 dB.");
        }

        if (ExpanderRatio is < 1 or > 10)
        {
            throw new InvalidOperationException("Expander ratio must be between 1:1 and 10:1.");
        }

        if (LowMidEqDb is < -12 or > 12 || PresenceEqDb is < -12 or > 12)
        {
            throw new InvalidOperationException("Microphone EQ gains must be between -12 dB and 12 dB.");
        }

        if (CompressorThresholdDb is < -60 or > 0)
        {
            throw new InvalidOperationException("Compressor threshold must be between -60 dB and 0 dB.");
        }

        if (CompressorRatio is < 1 or > 20)
        {
            throw new InvalidOperationException("Compressor ratio must be between 1:1 and 20:1.");
        }

        if (CompressorMakeupGainDb is < 0 or > 18)
        {
            throw new InvalidOperationException("Compressor makeup gain must be between 0 dB and 18 dB.");
        }

        if (DeEsserThresholdDb is < -60 or > 0)
        {
            throw new InvalidOperationException("De-esser threshold must be between -60 dB and 0 dB.");
        }

        if (LimiterThresholdDb is < -20 or > 0)
        {
            throw new InvalidOperationException("Limiter threshold must be between -20 dB and 0 dB.");
        }
    }
}
