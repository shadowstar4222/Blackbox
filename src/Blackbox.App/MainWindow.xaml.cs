using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Blackbox.App.Hotkeys;
using Blackbox.Domain;
using Blackbox.Recording;
using Blackbox.Storage;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WPF window owns and disposes its cancellation source during the guarded closing lifecycle.")]
public partial class MainWindow : Window
{
    private readonly RecordingCoordinator _coordinator;
    private readonly AutomaticCaptureService _automaticCaptureService;
    private readonly ObsAutoSetupService _obsAutoSetupService;
    private readonly StartupRecoveryCoordinator _startupRecoveryCoordinator;
    private readonly AudioConfigurationService _audioConfigurationService;
    private readonly ProtectionService _protectionService;
    private readonly StorageQuotaEnforcer _storageQuotaEnforcer;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly RecordingSettings _settings;
    private readonly Func<MicrophoneCalibrationWindow> _microphoneCalibrationWindowFactory;
    private readonly Func<RecordingLibraryWindow> _recordingLibraryWindowFactory;
    private readonly Func<GameProfilesWindow> _gameProfilesWindowFactory;
    private readonly Func<DiagnosticsWindow> _diagnosticsWindowFactory;
    private readonly ILogger<MainWindow> _logger;
    private RecordingLibraryWindow? _recordingLibraryWindow;
    private GameProfilesWindow? _gameProfilesWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private readonly CancellationTokenSource _startupRecoveryCancellation = new();
    private bool _obsReady;
    private bool _isSetupBusy;
    private bool _isRecoveryBusy = true;
    private bool _isShutdownInProgress;
    private bool _shutdownComplete;

    public MainWindow(
        RecordingCoordinator coordinator,
        AutomaticCaptureService automaticCaptureService,
        ObsAutoSetupService obsAutoSetupService,
        StartupRecoveryCoordinator startupRecoveryCoordinator,
        AudioConfigurationService audioConfigurationService,
        ProtectionService protectionService,
        StorageQuotaEnforcer storageQuotaEnforcer,
        GlobalHotkeyService hotkeyService,
        RecordingSettings settings,
        Func<MicrophoneCalibrationWindow> microphoneCalibrationWindowFactory,
        Func<RecordingLibraryWindow> recordingLibraryWindowFactory,
        Func<GameProfilesWindow> gameProfilesWindowFactory,
        Func<DiagnosticsWindow> diagnosticsWindowFactory,
        ILogger<MainWindow> logger)
    {
        _coordinator = coordinator;
        _automaticCaptureService = automaticCaptureService;
        _obsAutoSetupService = obsAutoSetupService;
        _startupRecoveryCoordinator = startupRecoveryCoordinator;
        _audioConfigurationService = audioConfigurationService;
        _protectionService = protectionService;
        _storageQuotaEnforcer = storageQuotaEnforcer;
        _hotkeyService = hotkeyService;
        _settings = settings;
        _microphoneCalibrationWindowFactory = microphoneCalibrationWindowFactory;
        _recordingLibraryWindowFactory = recordingLibraryWindowFactory;
        _gameProfilesWindowFactory = gameProfilesWindowFactory;
        _diagnosticsWindowFactory = diagnosticsWindowFactory;
        _logger = logger;
        InitializeComponent();
        _automaticCaptureService.StatusChanged += AutomaticCaptureService_StatusChanged;
        SegmentLengthText.Text = $"{_settings.SegmentDurationMinutes} minutes";
        LocationText.Text = _settings.RecordingLocation;
        Loaded += MainWindow_Loaded;
        UpdateRecordingControls();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SetupProgressBar.Visibility = Visibility.Visible;
        SetupProgressBar.IsIndeterminate = true;
        var progress = new Progress<string>(message => StatusText.Text = message);
        try
        {
            var outcome = await _startupRecoveryCoordinator.RunAsync(
                progress,
                _startupRecoveryCancellation.Token);
            _obsReady = outcome.ObsReady;
            StatusText.Text = outcome.Message;
            AudioButton.IsEnabled = outcome.ObsReady;
            CalibrateButton.IsEnabled = outcome.ObsReady;
            ObsSetupButton.Content = outcome.ObsReady ? "Check OBS" : "Setup OBS";
        }
        catch (OperationCanceledException) when (_startupRecoveryCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportCommandFailure("Startup recovery", ex);
        }
        finally
        {
            _isRecoveryBusy = false;
            SetupProgressBar.Visibility = Visibility.Collapsed;
            UpdateRecordingControls();
        }
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
            StatusText.Text = "Recording";
            UpdateRecordingControls();
        });
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync("Stop recording", async () =>
        {
            if (_automaticCaptureService.IsEnabled)
            {
                await _automaticCaptureService.SetEnabledAsync(false);
            }

            await _coordinator.StopAsync();
            StatusText.Text = "Idle";
            UpdateRecordingControls();
        });
    }

    private async void AutoCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync("Toggle automatic capture", async () =>
        {
            await _automaticCaptureService.SetEnabledAsync(!_automaticCaptureService.IsEnabled);
            UpdateRecordingControls();
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

    private void RecordingLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_recordingLibraryWindow is not null)
            {
                _recordingLibraryWindow.Activate();
                return;
            }

            var window = _recordingLibraryWindowFactory();
            window.Owner = this;
            window.Closed += (_, _) => _recordingLibraryWindow = null;
            _recordingLibraryWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ReportCommandFailure("Open recordings", ex);
        }
    }

    private void GamesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_gameProfilesWindow is not null)
            {
                _gameProfilesWindow.Activate();
                return;
            }

            var window = _gameProfilesWindowFactory();
            window.Owner = this;
            window.Closed += (_, _) => _gameProfilesWindow = null;
            _gameProfilesWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ReportCommandFailure("Open games", ex);
        }
    }

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_diagnosticsWindow is not null)
            {
                _diagnosticsWindow.Activate();
                return;
            }

            var window = _diagnosticsWindowFactory();
            window.Owner = this;
            window.Closed += (_, _) => _diagnosticsWindow = null;
            _diagnosticsWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ReportCommandFailure("Open diagnostics", ex);
        }
    }

    private async void ObsSetupButton_Click(object sender, RoutedEventArgs e)
    {
        _isSetupBusy = true;
        ObsSetupButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        AutoCaptureButton.IsEnabled = false;
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
            _obsReady = result.IsSuccessful;
            StatusText.Text = result.Message;
            AudioButton.IsEnabled = result.IsSuccessful;
            CalibrateButton.IsEnabled = result.IsSuccessful;
            AutoCaptureButton.IsEnabled = result.IsSuccessful;
            ObsSetupButton.Content = result.IsSuccessful ? "Check OBS" : "Retry OBS Setup";
            UpdateRecordingControls();
        }
        catch (Exception ex)
        {
            ReportCommandFailure("OBS setup", ex);
        }
        finally
        {
            _isSetupBusy = false;
            SetupProgressBar.Visibility = Visibility.Collapsed;
            UpdateRecordingControls();
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

    private void AutomaticCaptureService_StatusChanged(AutomaticCaptureStatus status)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            AutomaticCaptureStatusText.Text = status.Message;
            CurrentGameText.Text = status.Target?.Title ?? "None detected";
            if (_automaticCaptureService.IsEnabled || status.State == AutomaticCaptureState.Faulted)
            {
                StatusText.Text = status.Message;
            }
            else if (status.State == AutomaticCaptureState.Disabled && !_coordinator.IsRecording)
            {
                StatusText.Text = "Idle";
            }

            UpdateRecordingControls();
        });
    }

    private void UpdateRecordingControls()
    {
        var automatic = _automaticCaptureService.IsEnabled;
        var recording = _coordinator.IsRecording;
        var busy = _isSetupBusy || _isRecoveryBusy;
        AutoCaptureButton.Content = automatic ? "Disable Auto" : "Enable Auto";
        ObsSetupButton.IsEnabled = !busy && !automatic && !recording;
        AutoCaptureButton.IsEnabled = _obsReady && !busy;
        StartButton.IsEnabled = _obsReady && !busy && !automatic && !recording;
        StopButton.IsEnabled = recording;
        DiagnosticsButton.IsEnabled = !_isRecoveryBusy;
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_shutdownComplete)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_isShutdownInProgress)
        {
            return;
        }

        _isShutdownInProgress = true;
        IsEnabled = false;
        await _startupRecoveryCancellation.CancelAsync();
        _hotkeyService.Dispose();
        _automaticCaptureService.StatusChanged -= AutomaticCaptureService_StatusChanged;

        if (_automaticCaptureService.IsEnabled)
        {
            try
            {
                await _automaticCaptureService.SetEnabledAsync(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not stop automatic capture while Blackbox was closing.");
            }
        }

        if (_coordinator.IsRecording)
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

        _startupRecoveryCancellation.Dispose();
        _shutdownComplete = true;
        Close();
    }
}
