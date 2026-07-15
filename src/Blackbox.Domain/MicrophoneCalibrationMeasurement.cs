namespace Blackbox.Domain;

public sealed record MicrophoneCalibrationMeasurement(
    MicrophoneCalibrationPhase Phase,
    double PeakDb,
    double RmsDb,
    int SampleCount);
