using System.Runtime.InteropServices;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Collections.Concurrent;

namespace Blackbox.Capture;

public sealed class NativeMicrophoneController(NativeRecordingController recorder, IMicrophoneConfigurationStore configurationStore)
    : IObsMicrophoneController, IObsAudioMeterClient
{
    private string? _deviceId;
    private MicrophoneProcessingSettings _settings = configurationStore.Current.ProcessingSettings;
    private bool _muted;

    public Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<MicrophoneDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device) result.Add(new MicrophoneDevice(device.ID, device.FriendlyName));
        }
        return Task.FromResult<IReadOnlyList<MicrophoneDevice>>(result);
    }

    public Task<MicrophoneDeviceStatus> GetDeviceStatusAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = OpenDevice(enumerator, deviceId);
            return Task.FromResult(new MicrophoneDeviceStatus(device.ID,
                device.State == DeviceState.Active ? MicrophoneConnectionState.Connected : MicrophoneConnectionState.Disconnected,
                DateTimeOffset.UtcNow));
        }
        catch (COMException) { return Task.FromResult(new MicrophoneDeviceStatus(deviceId, MicrophoneConnectionState.Disconnected, DateTimeOffset.UtcNow)); }
    }

    public async Task ConfigureAsync(MicrophoneDevice device, MicrophoneProcessingSettings processingSettings, CancellationToken cancellationToken = default)
    {
        processingSettings.Validate();
        _deviceId = device.Id;
        _settings = processingSettings;
        await recorder.ConfigureMicrophoneAsync(_deviceId, _settings, _muted, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        _muted = muted;
        await recorder.ConfigureMicrophoneAsync(_deviceId, _settings, muted, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetProcessingEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _settings = _settings with { ProcessingEnabled = enabled };
        await recorder.ConfigureMicrophoneAsync(_deviceId, _settings, _muted, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsRecordingAsync(CancellationToken cancellationToken = default) =>
        (await recorder.GetRecordingStatusAsync(cancellationToken).ConfigureAwait(false)).IsActive;

    public async Task<IReadOnlyList<AudioLevelSnapshot>> CaptureLevelsAsync(TimeSpan duration,
        IProgress<AudioLevelSnapshot>? progress = null, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(duration));
        using var enumerator = new MMDeviceEnumerator();
        using var device = OpenDevice(enumerator, _deviceId ?? configurationStore.Current.DeviceId);
        var deviceName = device.FriendlyName;
        var levels = new ConcurrentQueue<AudioLevelSnapshot>();
        using var capture = new WasapiCapture(device);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.DataAvailable += (_, data) =>
        {
            try
            {
                var measured = PcmAudioLevels.Measure(data.Buffer.AsSpan(0, data.BytesRecorded), capture.WaveFormat);
                var snapshot = new AudioLevelSnapshot(deviceName, measured.PeakDb, measured.RmsDb, DateTimeOffset.UtcNow);
                levels.Enqueue(snapshot);
                progress?.Report(snapshot);
            }
            catch (Exception ex) { stopped.TrySetException(ex); }
        };
        capture.RecordingStopped += (_, result) =>
        {
            if (result.Exception is { } error) stopped.TrySetException(error);
            else stopped.TrySetResult();
        };
        capture.StartRecording();
        try
        {
            var durationTask = Task.Delay(duration, cancellationToken);
            await await Task.WhenAny(durationTask, stopped.Task).ConfigureAwait(false);
        }
        finally
        {
            capture.StopRecording();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        return levels.ToArray();
    }

    public Task<IReadOnlyList<AudioLevelSnapshot>> CaptureInputLevelsAsync(ObsConnectionSettings settings, string inputName,
        TimeSpan duration, IProgress<AudioLevelSnapshot>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Per-source live audio meters are not yet available in the native recorder.");

    private static MMDevice OpenDevice(MMDeviceEnumerator enumerator, string deviceId) =>
        deviceId.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : enumerator.GetDevice(deviceId);
}
