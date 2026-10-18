using System.IO;
using System.Text.Json;
using System.Windows;
using Blackbox.Infrastructure;

namespace Blackbox.App;

public partial class MainWindow
{
    internal async Task ValidateAutomaticCaptureAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        Show();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (_isRecoveryBusy || _isSetupBusy) await Task.Delay(50, timeout.Token);
        var preferences = _experienceSettingsStore.Current;
        try
        {
            await SetAutomaticCaptureAsync(false, persistPreference: false, timeout.Token);
            _recorderReady = false;
            UpdateRecordingControls();
            if (!AutoCaptureButton.IsEnabled || !WatchGamesToggle.IsEnabled)
                throw new InvalidOperationException("Auto capture cannot initiate its own recorder check.");
            await SetAutomaticCaptureAsync(true, persistPreference: false, timeout.Token);
            if (!_recorderReady || !_automaticCaptureService.IsEnabled)
                throw new InvalidOperationException("Enabling auto did not check the recorder and start watching.");
            await SetAutomaticCaptureAsync(false, persistPreference: false, timeout.Token);

            // Manual setup remains available, and enabling auto must not record its warning.
            await OpenRecordingSetupAsync(moving: false, 30, timeout.Token);
            _setupMonitor.Stop();
            await SetAutomaticCaptureAsync(true, persistPreference: false, timeout.Token);
            await UpdateRecordingSetupAsync();
            if (_setupProcess is null || !IsAutomaticRecordingEnabled || _coordinator.IsRecording)
                throw new InvalidOperationException("Setup auto mode closed the helper or recorded before scene consent.");
            await SaveWorkspaceImageAsync(outputDirectory, "auto-waiting-for-scene");
            await SetAutomaticCaptureAsync(false, persistPreference: false, timeout.Token);
            var target = _manualTargetResolver.TemporaryTarget! with { CaptureGameAudio = false };
            var profile = (await _profileSettings.ResolveAsync(target, timeout.Token)) with { CaptureMicrophone = false };
            var settings = _settings with { RecordingLocation = Path.Combine(_settings.RecordingLocation, "Auto Control Fixture") };
            if (!await _coordinator.TryStartAsync(settings, target, profile, timeout.Token))
                throw new InvalidOperationException("Could not start the silent manual setup fixture.");
            await SetAutomaticCaptureAsync(true, persistPreference: false, timeout.Token);
            await SetAutomaticCaptureAsync(false, persistPreference: false, timeout.Token);
            if (!_coordinator.IsRecording || _setupProcess is null || _setupAutoRecording)
                throw new InvalidOperationException("Auto controls interrupted a manual setup recording.");
            await _coordinator.TryStopAsync(timeout.Token);
            await CloseRecordingSetupAsync(restoreAutomatic: false);

            // The helper starts a still, silent scene; the parent uses real readiness IPC.
            await SetAutomaticCaptureAsync(true, persistPreference: false, timeout.Token);
            _validatingSetup = true;
            await OpenRecordingSetupAsync(moving: false, 30, timeout.Token);
            _setupMonitor.Stop();
            _manualTargetResolver.SetTemporaryTarget(_manualTargetResolver.TemporaryTarget! with { CaptureGameAudio = false });
            if (!IsAutomaticRecordingEnabled || _automaticCaptureService.IsEnabled || AutoCaptureButton.Content as string != "Disable auto")
                throw new InvalidOperationException("Setup lost the enabled auto mode while isolating its target.");
            while (RecordingSetupProtocol.Read<RecordingSetupSceneState>(Path.Combine(_setupDirectory!, "scene-state.json"))?.IsReadyFor(_setupProcess!.Id) != true)
                await Task.Delay(50, timeout.Token);
            await UpdateRecordingSetupAsync();
            if (!_coordinator.IsRecording || !_setupAutoRecording)
                throw new InvalidOperationException("Scene readiness did not automatically start recording.");
            if (_obsController is not INativeRecorderController { AudioSignal: { MixEnabled: false, MicrophoneEnabled: false } })
                throw new InvalidOperationException("The setup auto-start fixture was not video-only.");
            await SaveWorkspaceImageAsync(outputDirectory, "setup-recording-automatically");
            await SetAutomaticCaptureAsync(false, persistPreference: false, timeout.Token);
            if (_coordinator.IsRecording || _setupProcess is null)
                throw new InvalidOperationException("Disabling setup auto did not stop its owned recording safely.");

            await SetAutomaticCaptureAsync(true, persistPreference: false, timeout.Token);
            await UpdateRecordingSetupAsync();
            if (!_coordinator.IsRecording) throw new InvalidOperationException("Re-enabling setup auto did not re-arm capture.");
            await StopRecordingCoreAsync(timeout.Token);
            await UpdateRecordingSetupAsync();
            await UpdateRecordingSetupAsync();
            if (_coordinator.IsRecording || _setupAutoRecordArmed || !IsAutomaticRecordingEnabled)
                throw new InvalidOperationException("Stop restarted setup recording or lost the desired auto mode.");
            await CloseRecordingSetupAsync(restoreAutomatic: true);
            _validatingSetup = false;
            if (_manualTargetResolver.TemporaryTarget is not null || !_automaticCaptureService.IsEnabled)
                throw new InvalidOperationException("Closing setup did not restore remembered-game watching.");
            if (_experienceSettingsStore.Current != preferences)
                throw new InvalidOperationException("Temporary setup mode changes modified startup preferences.");
            await SaveWorkspaceImageAsync(outputDirectory, "auto-watching-after-setup");
            _isSetupBusy = true;
            UpdateRecordingControls();
            if (AutoCaptureButton.IsEnabled || WatchGamesToggle.IsEnabled)
                throw new InvalidOperationException("Auto controls ignored an active recorder operation.");
            _isSetupBusy = false;
            UpdateRecordingControls();
            await SetAutomaticCaptureAsync(false, persistPreference: false, timeout.Token);
            await SetAutomaticCaptureAsync(true, persistPreference: true, timeout.Token);
            if (!_experienceSettingsStore.Current.WatchRememberedGames || WatchGamesToggle.IsChecked != true)
                throw new InvalidOperationException("Explicit enabling did not save the startup preference.");
            File.WriteAllText(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(new
            {
                RecorderCheckOnEnable = true, SetupControlsAvailable = true, WarningNotRecordedAutomatically = true,
                ManualSetupRecordingPreserved = true, AutoModeCarriedIntoSetup = true, RealSceneReadinessStartsCapture = true,
                GuardFixturesVideoOnly = true, DisableStopsOwnedAutoRecording = true, ReenableRearmsSetup = true,
                StopDoesNotRestartSetup = true, ClosingRestoresGameWatcher = true, TemporaryTargetCleared = true,
                BusyOperationGuarded = true, StartupPreferenceSaved = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            _isSetupBusy = false;
            if (_coordinator.IsRecording) await _coordinator.TryStopAsync();
            await CloseRecordingSetupAsync(restoreAutomatic: false);
            await SetAutomaticCaptureAsync(false, persistPreference: false);
            _validatingSetup = false;
            SaveExperienceSettings(preferences);
        }
    }
}
