using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Blackbox.Capture;
using Blackbox.Domain;
using Blackbox.Export;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

public partial class MainWindow
{
    private Process? _setupProcess;
    private string? _setupDirectory;
    private readonly DispatcherTimer _setupMonitor = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _setupMonitorBusy;
    private bool _setupRestoresAutomatic;
    private bool _setupAutoRecordArmed;
    private bool _setupAutoRecording;
    private string? _lastSetupCommand;
    private RecordingSetupStatus? _lastSetupStatus;
    private string _setupMessage = "Press Record to save a test. Close this window to restore automatic capture.";
    private (int Width, int Height)? _setupOutput;
    private DateTimeOffset _setupRecordingStartedAt;
    private bool _openingRecordingSetup;
    private bool _validatingSetup;
    private bool IsAutomaticRecordingEnabled => _automaticCaptureService.IsEnabled ||
        (_setupProcess is not null && _setupRestoresAutomatic);

    private async Task OpenRecordingSetupAsync(bool moving, int sceneFps, CancellationToken cancellationToken)
    {
        if (_openingRecordingSetup) return;
        _openingRecordingSetup = true;
        try { await OpenRecordingSetupCoreAsync(moving, sceneFps, cancellationToken); }
        finally { _openingRecordingSetup = false; UpdateRecordingControls(); }
    }

    private async Task OpenRecordingSetupCoreAsync(bool moving, int sceneFps, CancellationToken cancellationToken)
    {
        if (_setupProcess is not null) throw new InvalidOperationException("The recording setup window is already open.");
        if (_coordinator.IsRecording || _coordinator.IsTransitioning || _isRecordingCommandBusy || _isAutomaticCaptureCommandBusy || _isSetupBusy)
            throw new InvalidOperationException("Stop the current recording before opening the setup window.");
        var executable = Path.Combine(AppContext.BaseDirectory, "Blackbox.SetupTarget.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The recording setup component is missing. Use the complete Blackbox package.", executable);
        var customData = Environment.GetEnvironmentVariable("BLACKBOX_DATA_DIRECTORY");
        var appData = string.IsNullOrWhiteSpace(customData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blackbox")
            : Path.GetFullPath(customData);
        var directory = Path.Combine(appData, "recording-setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var music = Path.Combine(appData, "recording-setup", "moonlit-circuit-v2.wav");
        if (!File.Exists(music))
        {
            await Task.Run(() =>
            {
                var temporary = music + ".tmp";
                try
                {
                    using (var stream = File.Create(temporary)) RecordingSetupMusic.WriteWave(stream, cancellationToken);
                    File.Move(temporary, music, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (_coordinator.IsRecording || _coordinator.IsTransitioning)
            throw new InvalidOperationException("A recording started while preparing setup. Stop it before opening the setup window.");
        _setupRestoresAutomatic = _automaticCaptureService.IsEnabled;
        if (_setupRestoresAutomatic) await SetAutomaticCaptureAsync(false, persistPreference: false, cancellationToken);
        try
        {
            _setupDirectory = directory;
            _lastSetupStatus = null;
            _lastSetupCommand = null;
            _setupOutput = null;
            _setupAutoRecordArmed = _setupRestoresAutomatic;
            _setupAutoRecording = false;
            _setupMessage = _setupRestoresAutomatic
                ? "Automatic recording will start when you start the scene."
                : "Press Record to save a test. Close this window to return to Blackbox.";
            using var current = Process.GetCurrentProcess();
            var config = new RecordingSetupConfiguration(current.Id, current.StartTime.ToUniversalTime().Ticks,
                directory, moving, sceneFps, music, Validate: _validatingSetup,
                ValidateAudio: _validatingSetup && Environment.GetEnvironmentVariable("BLACKBOX_SETUP_LIVE_TEST") == "1");
            var configPath = Path.Combine(directory, "configuration.json");
            RecordingSetupProtocol.Write(configPath, config);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--recording-setup-target"); start.ArgumentList.Add(configPath);
            _setupProcess = Process.Start(start) ?? throw new InvalidOperationException("Could not open the recording setup window.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                _setupProcess.Refresh();
                if (_setupProcess.HasExited) throw new InvalidOperationException("The recording setup window exited before becoming ready.");
                var handle = _setupProcess.MainWindowHandle.ToInt64();
                if (handle != 0 && await _applications.GetRunningApplicationAsync(handle, timeout.Token) is { } target &&
                    target.ProcessId == _setupProcess.Id)
                {
                    _manualTargetResolver.SetTemporaryTarget(target.ToCaptureTarget(GameDetectionSource.ConfiguredExecutable) with { Title = "Blackbox Recording Setup" });
                    break;
                }
                await Task.Delay(100, timeout.Token);
            }
            _setupMonitor.Tick -= RecordingSetupMonitor_Tick;
            _setupMonitor.Tick += RecordingSetupMonitor_Tick;
            _setupMonitor.Start();
            await UpdateRecordingSetupAsync();
            UpdateRecordingControls();
        }
        catch { await CloseRecordingSetupAsync(restoreAutomatic: true); throw; }
    }

    private async void RecordingSetupMonitor_Tick(object? sender, EventArgs e)
    {
        if (_setupMonitorBusy || _isAutomaticCaptureCommandBusy || _isShutdownInProgress) return;
        _setupMonitorBusy = true;
        try
        {
            if (_setupProcess?.HasExited == true) await CloseRecordingSetupAsync(restoreAutomatic: true);
            else if (_setupProcess is not null) await UpdateRecordingSetupAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not update recording setup status."); _setupMessage = ex.GetBaseException().Message; }
        finally { _setupMonitorBusy = false; }
    }

    private async Task UpdateRecordingSetupAsync()
    {
        if (_setupDirectory is null || _manualTargetResolver.TemporaryTarget is not { } selected) return;
        var command = RecordingSetupProtocol.Read<RecordingSetupCommand>(Path.Combine(_setupDirectory, "command.json"));
        if (command is not null && command.Id != _lastSetupCommand)
        {
            _lastSetupCommand = command.Id;
            if (command.Action == "record" && !_coordinator.IsRecording)
            {
                await StartSetupRecordingAsync(automatic: false);
            }
            else if (command.Action == "stop" && _coordinator.IsRecording &&
                     (_obsController as INativeRecorderController)?.Target?.ProcessId == selected.ProcessId)
            {
                _setupAutoRecordArmed = false;
                await StopRecordingAsync();
                _setupMessage = "Saved. Open Recordings in Blackbox to review your setup test.";
            }
        }
        var scene = RecordingSetupProtocol.Read<RecordingSetupSceneState>(Path.Combine(_setupDirectory, "scene-state.json"));
        if (_setupRestoresAutomatic && _setupAutoRecordArmed && !_openingRecordingSetup &&
            !_coordinator.IsRecording && !_coordinator.IsTransitioning &&
            scene?.IsReadyFor(_setupProcess?.Id ?? 0) == true)
            await StartSetupRecordingAsync(automatic: true);
        var window = await _applications.GetRunningApplicationAsync(selected.WindowHandle);
        var quality = _qualityContext.Current.ForNativeRecording();
        var size = NativeRecordingSizing.Resolve(quality.Resolution, window?.WindowWidth ?? selected.WindowWidth, window?.WindowHeight ?? selected.WindowHeight);
        var recording = _coordinator.IsRecording && (_obsController as INativeRecorderController)?.Target?.ProcessId == selected.ProcessId;
        if (recording) _setupOutput ??= size;
        var output = recording ? _setupOutput!.Value : size;
        var snapshot = _recordingGuardian.Snapshot;
        var currentSnapshot = snapshot.RecordingOutputActive && snapshot.TargetTitle == selected.Title && snapshot.CapturedAt >= _setupRecordingStartedAt;
        if (recording && currentSnapshot && snapshot.OutputWidth > 0)
            output = (snapshot.OutputWidth, snapshot.OutputHeight);
        var mic = _microphoneConfiguration.Current;
        var processing = mic.ProcessingSettings;
        var microphone = recording
            ? !currentSnapshot ? "Microphone: checking recorder..." : snapshot.MicrophoneCaptureEnabled ? snapshot.MicrophonePacketReceived ? "Microphone: receiving audio" : "Microphone: waiting for audio" : "Microphone: off"
            : "Microphone: " + mic.DeviceName;
        var status = new RecordingSetupStatus(recording ? "RECORDING / APPLIED" : "READY / CONFIGURED",
            $"{output.Width} x {output.Height} | {quality.FramesPerSecond} fps\nH.264 | {NativeRecordingSizing.VideoBitrate(output.Width, output.Height, quality.FramesPerSecond) / 1_000_000.0:0.0} Mbps target\nHardware encoding requested",
            $"AAC | {quality.AudioBitrateKbps} kbps\n48 kHz | Stereo\n{(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) ? "Application audio" : "System audio (all playing apps)")}",
            microphone,
            recording && currentSnapshot && !snapshot.MicrophoneCaptureEnabled ? "Voice processing: off (no microphone)" :
                processing.ProcessingEnabled ? $"Voice: {processing.Preset} | {processing.InputGainDb:+0.#;-0.#;0} dB gain" : "Voice processing: off",
            _setupMessage, !recording && !_openingRecordingSetup && !_isRecoveryBusy && !_isSetupBusy &&
                !_isAutomaticCaptureCommandBusy && !_isRecordingCommandBusy && !_coordinator.IsTransitioning,
            recording && !_coordinator.IsTransitioning, _setupRestoresAutomatic);
        if (status != _lastSetupStatus)
        {
            RecordingSetupProtocol.Write(Path.Combine(_setupDirectory, "status.json"), status);
            _lastSetupStatus = status;
        }
    }

    private async Task StartSetupRecordingAsync(bool automatic)
    {
        // A start is one-shot: Stop or a failed attempt must not create a restart loop.
        _setupAutoRecordArmed = false;
        _setupOutput = null;
        _setupRecordingStartedAt = DateTimeOffset.UtcNow;
        await StartRecordingAsync();
        _setupAutoRecording = automatic && _coordinator.IsRecording;
        _setupMessage = _coordinator.IsRecording
            ? "Recording this window. Stop, then open Recordings to review."
            : _recordingFailure ?? "Recording could not start. Press Record to retry or check diagnostics.";
    }

    private async Task CloseRecordingSetupAsync(bool restoreAutomatic)
    {
        _setupMonitor.Stop();
        var process = _setupProcess;
        _setupProcess = null;
        try
        {
            if (process is not null)
            {
                if (_coordinator.IsRecording && (_obsController as INativeRecorderController)?.Target?.ProcessId == process.Id)
                    await _coordinator.StopAsync();
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (TimeoutException) { process.Kill(); await process.WaitForExitAsync(); }
                }
                if (_obsController is NativeRecordingController recorder) await recorder.ClearIdleTargetAsync(process.Id);
            }
        }
        finally
        {
            process?.Dispose();
            _manualTargetResolver.SetTemporaryTarget(null);
            _setupDirectory = null;
            var restore = _setupRestoresAutomatic;
            _setupRestoresAutomatic = false;
            _setupAutoRecordArmed = _setupAutoRecording = false;
            if (restoreAutomatic && restore && !_isShutdownInProgress) await SetAutomaticCaptureAsync(true, persistPreference: false);
            UpdateRecordingControls();
        }
    }
}
