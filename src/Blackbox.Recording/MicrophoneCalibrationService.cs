using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class MicrophoneCalibrationService(
    IObsMicrophoneController microphoneController,
    IObsController obsController,
    IMicrophoneConfigurationStore configurationStore,
    MicrophoneCalibrationAnalyzer analyzer,
    ILogger<MicrophoneCalibrationService> logger)
{
    public Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default) =>
        microphoneController.GetDevicesAsync(cancellationToken);

    public async Task SelectDeviceAsync(
        MicrophoneDevice device,
        CancellationToken cancellationToken = default)
    {
        var configuration = configurationStore.Current with
        {
            DeviceId = device.Id,
            DeviceName = device.Name
        };
        await microphoneController.ConfigureAsync(device, configuration.ProcessingSettings, cancellationToken);
        configurationStore.Save(configuration);
    }

    public async Task<MicrophoneCalibrationMeasurement> CaptureAsync(
        MicrophoneCalibrationPhase phase,
        TimeSpan duration,
        IProgress<AudioLevelSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var snapshots = await microphoneController.CaptureLevelsAsync(
            duration,
            progress,
            cancellationToken);
        var measurement = analyzer.Measure(phase, snapshots);
        logger.LogInformation(
            "Captured microphone calibration phase {CalibrationPhase}: Peak={PeakDb:F1} dBFS, RMS={RmsDb:F1} dBFS, Samples={SampleCount}.",
            phase,
            measurement.PeakDb,
            measurement.RmsDb,
            measurement.SampleCount);
        return measurement;
    }

    public MicrophoneCalibrationResult Analyze(
        MicrophoneCalibrationMeasurement backgroundNoise,
        MicrophoneCalibrationMeasurement normalVoice,
        MicrophoneCalibrationMeasurement loudVoice) =>
        analyzer.Analyze(backgroundNoise, normalVoice, loudVoice);

    public async Task ApplyAsync(
        MicrophoneDevice device,
        MicrophoneCalibrationResult result,
        CancellationToken cancellationToken = default)
    {
        var processingSettings = result.CreateProcessingSettings();
        await microphoneController.ConfigureAsync(device, processingSettings, cancellationToken);
        configurationStore.Save(new MicrophoneConfiguration
        {
            DeviceId = device.Id,
            DeviceName = device.Name,
            ProcessingSettings = processingSettings
        });
        logger.LogInformation(
            "Applied microphone calibration for {MicrophoneDeviceName}. Gain={GainDb:F1}, Expander={ExpanderDb:F1}, Compressor={CompressorDb:F1}.",
            device.Name,
            result.RecommendedGainDb,
            result.RecommendedExpanderThresholdDb,
            result.RecommendedCompressorThresholdDb);
    }

    public async Task<MicrophoneComparisonResult> RecordComparisonAsync(
        MicrophoneDevice device,
        MicrophoneCalibrationResult result,
        TimeSpan sampleDuration,
        CancellationToken cancellationToken = default)
    {
        if (sampleDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleDuration));
        }

        if (await microphoneController.IsRecordingAsync(cancellationToken))
        {
            throw new InvalidOperationException("Stop the current recording before creating a microphone comparison.");
        }

        await ApplyAsync(device, result, cancellationToken);
        string beforePath;
        try
        {
            await microphoneController.SetProcessingEnabledAsync(false, cancellationToken);
            beforePath = await RecordSampleAsync(sampleDuration, cancellationToken);
        }
        finally
        {
            await microphoneController.SetProcessingEnabledAsync(true, CancellationToken.None);
        }

        var afterPath = await RecordSampleAsync(sampleDuration, cancellationToken);
        return new MicrophoneComparisonResult(beforePath, afterPath);
    }

    private async Task<string> RecordSampleAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var started = false;
        try
        {
            await obsController.StartRecordingAsync(cancellationToken);
            started = true;
            await Task.Delay(duration, cancellationToken);
            var path = await obsController.StopRecordingAsync(cancellationToken);
            started = false;
            return await RecordingOutputFileWaiter.WaitAsync(
                path,
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }
        finally
        {
            if (started)
            {
                try
                {
                    await obsController.StopRecordingAsync(CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "Could not stop a microphone comparison recording.");
                }
            }
        }
    }
}
