using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Blackbox.Infrastructure;

namespace Blackbox.App;

public partial class MainWindow
{
    internal async Task ValidateRecordingSetupAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        Show();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (_isRecoveryBusy || _isSetupBusy) await Task.Delay(50, timeout.Token);
        foreach (var size in new[] { new Size(1240, 780), new Size(1024, 680) })
        {
            Width = size.Width; Height = size.Height;
            await SelectNavigationAsync(DiagnosticsNavButton, OpenDiagnosticsPageAsync);
            _diagnosticsPage!.ValidateSetupConsent();
            await SaveWorkspaceImageAsync(outputDirectory, $"setup-options-{size.Width}");
        }
        _validatingSetup = true;
        try
        {
            if (!await CheckRecorderAsync()) throw new InvalidOperationException("Recorder check failed.");
            var preferences = _experienceSettingsStore.Current;
            await SetAutomaticCaptureAsync(true, persistPreference: false);
            await OpenRecordingSetupAsync(Environment.GetEnvironmentVariable("BLACKBOX_SETUP_LIVE_TEST") == "1", 30, timeout.Token);
            if (_automaticCaptureService.IsEnabled) throw new InvalidOperationException("Automatic capture was not paused for setup.");
            _setupMonitor.Stop();
            var directory = _setupDirectory!;
            while (!File.Exists(Path.Combine(directory, "scene-ready.json"))) await Task.Delay(100, timeout.Token);
            var resolved = await _manualTargetResolver.ResolveAsync(timeout.Token);
            if (resolved is null || resolved.ProcessId != _setupProcess!.Id)
                throw new InvalidOperationException("The setup window was not resolved as a temporary recording target.");
            RecordingSetupProtocol.Write(Path.Combine(directory, "command.json"), new RecordingSetupCommand("validation-record", "record"));
            await UpdateRecordingSetupAsync();
            if (!_coordinator.IsRecording) throw new InvalidOperationException("Setup Record command did not start recording.");
            var audioPeaks = new List<double>();
            HomeNavButton.IsChecked = true;
            SetCaptureHeader();
            for (var second = 0; second < 22; second++)
            {
                await Task.Delay(1000, timeout.Token);
                await UpdateRecordingSetupAsync();
                if (_obsController is INativeRecorderController { AudioSignal: { } signal })
                {
                    if (signal.MicrophoneEnabled) throw new InvalidOperationException("The recording test enabled microphone capture.");
                    if (signal.PeakDb is { } peak) audioPeaks.Add(peak);
                }
                if (second == 10)
                {
                    if (!_audioMeterTimer.IsEnabled || GuardianAudioMeterRow.Visibility != Visibility.Visible)
                        throw new InvalidOperationException("Live capture audio meter is not active.");
                    await SaveWorkspaceImageAsync(outputDirectory, "capture-live-audio");
                    await SelectNavigationAsync(DiagnosticsNavButton, OpenDiagnosticsPageAsync);
                    if (_audioMeterTimer.IsEnabled) throw new InvalidOperationException("Hidden capture kept its meter timer running.");
                    HomeNavButton.IsChecked = true;
                    SetCaptureHeader();
                }
            }
            if (Environment.GetEnvironmentVariable("BLACKBOX_SETUP_LIVE_TEST") == "1" &&
                (audioPeaks.Count < 10 || audioPeaks.Max() <= -60))
                throw new InvalidOperationException($"No useful encoder audio readings during music test: {audioPeaks.Count} readings.");
            RecordingSetupProtocol.Write(Path.Combine(directory, "command.json"), new RecordingSetupCommand("validation-stop", "stop"));
            await UpdateRecordingSetupAsync();
            if (_coordinator.IsRecording) throw new InvalidOperationException("Setup Stop command did not stop recording.");
            foreach (var file in Directory.GetFiles(directory, "*.png")) File.Copy(file, Path.Combine(outputDirectory, Path.GetFileName(file)), true);
            File.WriteAllText(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(new
            {
                TemporaryTargetResolved = true, CaptureThroughMainRecorder = true, MicrophoneDisabled = true,
                HelperProcessId = _setupProcess.Id, Directory = directory, Status = _lastSetupStatus,
                RecordingDirectory = _settings.RecordingLocation, SceneRunsUntilClosed = true,
                AudioPeakReadings = audioPeaks.Count, MinimumPeakDb = audioPeaks.Count == 0 ? (double?)null : audioPeaks.Min(),
                MaximumPeakDb = audioPeaks.Count == 0 ? (double?)null : audioPeaks.Max(),
                VisibleMeterVerified = true, HiddenMeterTimerStopped = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            await CloseRecordingSetupAsync(restoreAutomatic: true);
            if (_manualTargetResolver.TemporaryTarget is not null) throw new InvalidOperationException("Temporary target was not released.");
            if (!_automaticCaptureService.IsEnabled || _experienceSettingsStore.Current != preferences)
                throw new InvalidOperationException("Automatic capture or saved preferences were not restored.");
            await SetAutomaticCaptureAsync(false, persistPreference: false);
        }
        finally { await CloseRecordingSetupAsync(restoreAutomatic: false); _validatingSetup = false; }
    }
}

public partial class DiagnosticsPage
{
    internal void ValidateSetupConsent()
    {
        DiagnosticsTabs.SelectedIndex = 0;
        if (PlaybackTestConsentCheckBox.IsChecked == true || PlayTestButton.IsEnabled)
            throw new InvalidOperationException("Setup consent was retained.");
        DiagnosticsTabs.SelectedItem = SetupTab;
        PlayTestButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (_testCancellation is not null) throw new InvalidOperationException("Setup opened without consent.");
        PlaybackTestConsentCheckBox.IsChecked = true;
        if (!PlayTestButton.IsEnabled) throw new InvalidOperationException("Consent did not enable setup.");
        DiagnosticsTabs.SelectedIndex = 0;
        if (PlaybackTestConsentCheckBox.IsChecked == true || PlayTestButton.IsEnabled)
            throw new InvalidOperationException("Collapsing setup did not revoke consent.");
        DiagnosticsTabs.SelectedItem = SetupTab;
    }
}

internal sealed partial class RecordingSetupWindow
{
    internal async Task ValidateSetupWindowAsync()
    {
        await Task.Delay(600);
        if (_record.Style != FindResource("PrimaryButtonStyle") || _stop.Style != FindResource("DangerButtonStyle") ||
            _pause.Style != FindResource("IconButtonStyle") || Icon is null)
            throw new InvalidOperationException("The recording setup window did not inherit the Blackbox control theme.");
        if (!WarningVisible) throw new InvalidOperationException("The setup target did not start with a warning.");
        SaveImage("setup-warning");
        StartForValidation(_configuration.ValidateAudio);
        await Task.Delay(600);
        SaveImage("setup-scene");
        RecordingSetupProtocol.Write(Path.Combine(_configuration.Directory, "scene-ready.json"), new { Ready = true });
        await Task.Delay(5000);
        SaveImage("setup-recording-settings");
        Width = 900; Height = 600;
        await Task.Delay(1500);
        SaveImage("setup-compact");
        if (!SettingsText.Contains("AAC", StringComparison.Ordinal)) throw new InvalidOperationException("Recording settings were not received.");
        if (_configuration.Moving && _backgroundVideo?.HasVideo != true)
            throw new InvalidOperationException("The background video is not playing.");
        await Task.Delay(17000);
        if (_configuration.Moving && _videoLoops < 1)
            throw new InvalidOperationException("The background video did not loop.");
        RecordingSetupProtocol.Write(Path.Combine(_configuration.Directory, "loop-result.json"), new { VideoLoops = _videoLoops });
    }

    private void SaveImage(string name)
    {
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_configuration.Directory, name + ".png"));
        encoder.Save(stream);
    }
}
