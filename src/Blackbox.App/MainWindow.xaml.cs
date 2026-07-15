using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Blackbox.App.Hotkeys;
using Blackbox.Domain;
using Blackbox.Recording;
using Blackbox.Storage;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

public partial class MainWindow : Window
{
    private readonly RecordingCoordinator _coordinator;
    private readonly ObsAutoSetupService _obsAutoSetupService;
    private readonly AudioConfigurationService _audioConfigurationService;
    private readonly ProtectionService _protectionService;
    private readonly StorageQuotaEnforcer _storageQuotaEnforcer;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly RecordingSettings _settings;
    private readonly Func<MicrophoneCalibrationWindow> _microphoneCalibrationWindowFactory;
    private readonly ILogger<MainWindow> _logger;
    private bool _isRecording;

    public MainWindow(
        RecordingCoordinator coordinator,
        ObsAutoSetupService obsAutoSetupService,
        AudioConfigurationService audioConfigurationService,
        ProtectionService protectionService,
        StorageQuotaEnforcer storageQuotaEnforcer,
        GlobalHotkeyService hotkeyService,
        RecordingSettings settings,
        Func<MicrophoneCalibrationWindow> microphoneCalibrationWindowFactory,
        ILogger<MainWindow> logger)
    {
        _coordinator = coordinator;
        _obsAutoSetupService = obsAutoSetupService;
        _audioConfigurationService = audioConfigurationService;
        _protectionService = protectionService;
        _storageQuotaEnforcer = storageQuotaEnforcer;
        _hotkeyService = hotkeyService;
        _settings = settings;
        _microphoneCalibrationWindowFactory = microphoneCalibrationWindowFactory;
        _logger = logger;
        InitializeComponent();
        SegmentLengthText.Text = $"{_settings.SegmentDurationMinutes} minutes";
        LocationText.Text = _settings.RecordingLocation;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hotkeyService.Attach(new WindowInteropHelper(this));
        try
        {
            _hotkeyService.Register(
                new GlobalHotkey(1, HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.NoRepeat, 0x76),
                ProtectLastFiveMinutesAsync);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "The protection hotkey is unavailable.");
            StatusText.Text = "OBS setup required; protection hotkey unavailable";
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync("Start recording", async () =>
        {
            await _coordinator.StartAsync(_settings);
            _isRecording = true;
            StatusText.Text = "Recording";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
        });
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync("Stop recording", async () =>
        {
            await _coordinator.StopAsync();
            _isRecording = false;
            StatusText.Text = "Idle";
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        });
    }

    private async void ProtectButton_Click(object sender, RoutedEventArgs e)
    {
        await ProtectLastFiveMinutesAsync();
    }

    private async void PruneButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync("Apply storage quotas", async () =>
        {
            var result = await _storageQuotaEnforcer.EnforceAsync(_settings);
            StatusText.Text = $"Pruned {result.DeletedSegments} segment(s)";
        });
    }

    private async void AudioButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync("Apply audio routing", async () =>
        {
            await _audioConfigurationService.ApplyAsync(AudioRoutingProfile.Default, new MicrophoneProcessingSettings());
            StatusText.Text = "Applied audio routing";
        });
    }

    private void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = _microphoneCalibrationWindowFactory();
            window.Owner = this;
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            ReportCommandFailure("Open microphone calibration", ex);
        }
    }

    private void OpenRecordingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.RecordingLocation);
            Process.Start(new ProcessStartInfo(_settings.RecordingLocation) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ReportCommandFailure("Open recordings", ex);
        }
    }

    private async void ObsSetupButton_Click(object sender, RoutedEventArgs e)
    {
        ObsSetupButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        AudioButton.IsEnabled = false;
        SetupProgressBar.Visibility = Visibility.Visible;
        SetupProgressBar.IsIndeterminate = true;

        var progress = new Progress<ObsSetupProgress>(update =>
        {
            StatusText.Text = update.Message;
            SetupProgressBar.IsIndeterminate = update.Percent is null;
            if (update.Percent is not null)
            {
                SetupProgressBar.Value = update.Percent.Value;
            }
        });

        try
        {
            var result = await _obsAutoSetupService.SetupAsync(_settings, progress);
            StatusText.Text = result.Message;
            StartButton.IsEnabled = result.IsSuccessful;
            AudioButton.IsEnabled = result.IsSuccessful;
            CalibrateButton.IsEnabled = result.IsSuccessful;
            ObsSetupButton.Content = result.IsSuccessful ? "Check OBS" : "Retry OBS Setup";
        }
        catch (Exception ex)
        {
            ReportCommandFailure("OBS setup", ex);
        }
        finally
        {
            ObsSetupButton.IsEnabled = true;
            SetupProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ProtectLastFiveMinutesAsync()
    {
        await ExecuteCommandAsync("Protect previous 5 minutes", async () =>
        {
            await _protectionService.ProtectPreviousFiveMinutesAsync();
            StatusText.Text = "Protected previous 5 minutes";
        });
    }

    private async Task ExecuteCommandAsync(string commandName, Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (Exception ex)
        {
            ReportCommandFailure(commandName, ex);
        }
    }

    private void ReportCommandFailure(string commandName, Exception exception)
    {
        _logger.LogError(exception, "{CommandName} failed.", commandName);
        StatusText.Text = $"{commandName} failed: {exception.Message}";
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _hotkeyService.Dispose();

        if (_isRecording)
        {
            try
            {
                await _coordinator.StopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not stop recording while Blackbox was closing.");
            }
        }

        base.OnClosing(e);
    }
}
