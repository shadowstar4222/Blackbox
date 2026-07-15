namespace Blackbox.Domain;

public sealed record MicrophoneCalibrationResult(
    MicrophoneCalibrationMeasurement BackgroundNoise,
    MicrophoneCalibrationMeasurement NormalVoice,
    MicrophoneCalibrationMeasurement LoudVoice,
    double RecommendedGainDb,
    double RecommendedExpanderThresholdDb,
    double RecommendedCompressorThresholdDb,
    bool ClippingDetected,
    bool AutomaticGainControlSuspected)
{
    public MicrophoneProcessingSettings CreateProcessingSettings() => new()
    {
        InputGainDb = RecommendedGainDb,
        ExpanderThresholdDb = RecommendedExpanderThresholdDb,
        CompressorThresholdDb = RecommendedCompressorThresholdDb
    };
}
