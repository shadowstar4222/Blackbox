using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Blackbox.App.Hotkeys;
using Blackbox.Domain;
using Blackbox.Infrastructure;
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
    private static readonly Duration DrawerAnimationDuration =
        new(TimeSpan.FromMilliseconds(180));

    private readonly RecordingCoordinator _coordinator;
    private readonly AutomaticCaptureService _automaticCaptureService;
    private readonly ObsAutoSetupService _obsAutoSetupService;
    private readonly StartupRecoveryCoordinator _startupRecoveryCoordinator;
    private readonly AudioConfigurationService _audioConfigurationService;
    private readonly ProtectionService _protectionService;
    private readonly StorageQuotaEnforcer _storageQuotaEnforcer;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly RecordingSettings _settings;
    private readonly UserExperienceSettingsStore _experienceSettingsStore;
    private readonly IWindowsStartupManager _windowsStartupManager;
    private readonly TrayIconService _trayIconService;
    private readonly Func<MicrophoneCalibrationWindow> _microphoneCalibrationWindowFactory;
    private readonly Func<RecordingLibraryWindow> _recordingLibraryWindowFactory;
    private readonly Func<GameProfilesWindow> _gameProfilesWindowFactory;
    private readonly Func<DiagnosticsWindow> _diagnosticsWindowFactory;
    private readonly ILogger<MainWindow> _logger;
    private readonly CancellationTokenSource _startupRecoveryCancellation = new();
    private RecordingLibraryWindow? _recordingLibraryWindow;
    private GameProfilesWindow? _gameProfilesWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private bool _obsReady;
    private bool _isSetupBusy;
    private bool _isRecoveryBusy = true;
    private bool _isShutdownInProgress;
    private bool _shutdownComplete;
    private bool _exitRequested;
    private bool _isLoadingExperienceSettings;
    private bool _drawerIsOpen;

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
        UserExperienceSettingsStore experienceSettingsStore,
        IWindowsStartupManager windowsStartupManager,
        TrayIconService trayIconService,
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
        _experienceSettingsStore = experienceSettingsStore;
        _windowsStartupManager = windowsStartupManager;
        _trayIconService = trayIconService;
        _microphoneCalibrationWindowFactory = microphoneCalibrationWindowFactory;
        _recordingLibraryWindowFactory = recordingLibraryWindowFactory;
        _gameProfilesWindowFactory = gameProfilesWindowFactory;
        _diagnosticsWindowFactory = diagnosticsWindowFactory;
        _logger = logger;

        InitializeComponent();
        _automaticCaptureService.StatusChanged += AutomaticCaptureService_StatusChanged;
        _trayIconService.CommandRequested += TrayIconService_CommandRequested;
        SegmentLengthText.Text = $"{_settings.SegmentDurationMinutes} minutes";
        LocationText.Text = _settings.RecordingLocation;
        LoadExperienceSettings();
        Loaded += MainWindow_Loaded;
        UpdateRecordingControls();
    }

    public bool StartHidden { get; set; }

    public void PrepareForSystemShutdown() => _exitRequested = true;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (StartHidden)
        {
            HideToTray(showNotification: false);
        }

        SetupProgressBar.Visibility = Visibility.Visible;
        SetupProgressBar.IsIndeterminate = true;
        var progress = new Progress<string>(message =>
        {
            StatusText.Text = message;
            DrawerSystemStatusText.Text = message;
        });
        try
        {
            var outcome = await _startupRecoveryCoordinator.RunAsync(
                progress,
                _startupRecoveryCancellation.Token);
            _obsReady = outcome.ObsReady;
            SetStatus(outcome.Message);
            ObsSetupButton.Content = outcome.ObsReady ? "Check OBS" : "Setup OBS";

            if (_experienceSettingsStore.Current.WatchRememberedGames)
            {
                await EnsureAutomaticCaptureReadyAsync();
            }
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
        var helper = new WindowInteropHelper(this);
        WindowAppearance.ApplyDarkTitleBar(helper);
        _hotkeyService.Attach(helper);
        try
        {
            _hotkeyService.Register(
                new GlobalHotkey(
                    1,
                    HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.NoRepeat,
                    0x76),
                ProtectLastFiveMinutesAsync);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "The protection hotkey is unavailable.");
            SetStatus("Protection hotkey unavailable");
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) =>
        await StartRecordingAsync();

    private async Task StartRecordingAsync()
    {
        await ExecuteCommandAsync("Start recording", async () =>
        {
            await _coordinator.StartAsync(_settings);
            SetStatus("Recording");
            UpdateRecordingControls();
        });
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) =>
        await StopRecordingAsync();

    private async Task StopRecordingAsync()
    {
        await ExecuteCommandAsync("Stop recording", async () =>
        {
            if (_automaticCaptureService.IsEnabled)
            {
                await SetAutomaticCaptureAsync(false, persistPreference: true);
            }

            if (_coordinator.IsRecording)
            {
                await _coordinator.StopAsync();
            }

            SetStatus("Idle");
            UpdateRecordingControls();
        });
    }

    private async void AutoCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync(
            "Toggle automatic capture",
            () => SetAutomaticCaptureAsync(
                !_automaticCaptureService.IsEnabled,
                persistPreference: true));
    }

    private async Task SetAutomaticCaptureAsync(bool enabled, bool persistPreference)
    {
        if (enabled && !_obsReady && !await RunObsSetupAsync())
        {
            UpdateRecordingControls();
            return;
        }

        var previousState = _automaticCaptureService.IsEnabled;
        await _automaticCaptureService.SetEnabledAsync(enabled);
        try
        {
            if (persistPreference)
            {
                SaveExperienceSettings(
                    _experienceSettingsStore.Current with { WatchRememberedGames = enabled });
            }
        }
        catch (Exception saveException)
        {
            try
            {
                if (_automaticCaptureService.IsEnabled != previousState)
                {
                    await _automaticCaptureService.SetEnabledAsync(previousState);
                }
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "The remembered-game preference failed to save and the live watcher could not be restored.",
                    saveException,
                    rollbackException);
            }

            UpdateRecordingControls();
            throw;
        }

        UpdateRecordingControls();
    }

    private async Task EnsureAutomaticCaptureReadyAsync()
    {
        if (!_obsReady && !await RunObsSetupAsync())
        {
            return;
        }

        if (!_automaticCaptureService.IsEnabled)
        {
            await _automaticCaptureService.SetEnabledAsync(true);
        }

        UpdateRecordingControls();
    }

    private async void ProtectButton_Click(object sender, RoutedEventArgs e) =>
        await ProtectLastFiveMinutesAsync();

    private async void PruneButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync("Apply storage quotas", async () =>
        {
            var result = await _storageQuotaEnforcer.EnforceAsync(_settings);
            SetStatus($"Pruned {result.DeletedSegments} segment(s)");
        });
    }

    private async void AudioButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync("Apply audio routing", async () =>
        {
            await _audioConfigurationService.ApplyAsync(
                AudioRoutingProfile.Default,
                new MicrophoneProcessingSettings());
            SetStatus("Applied audio routing");
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
            Process.Start(new ProcessStartInfo(_settings.RecordingLocation)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ReportCommandFailure("Open recordings", ex);
        }
    }

    private void RecordingLibraryButton_Click(object sender, RoutedEventArgs e) =>
        OpenRecordingLibraryWindow();

    private void RecordingNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        CloseDrawer();
        CurrentViewTitleText.Text = "Recordings";
        CurrentViewSubtitleText.Text = "Continuous sessions, markers, playback, and export";
        OpenRecordingLibraryWindow();
    }

    private void OpenRecordingLibraryWindow()
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
            window.Closed += (_, _) =>
            {
                _recordingLibraryWindow = null;
                HomeNavButton.IsChecked = true;
                SetCaptureHeader();
            };
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
        GamesNavButton.IsChecked = true;
    }

    private void GamesNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ShowDrawer(DrawerSection.Games, "Games");
        }
    }

    private void OpenGameManagerButton_Click(object sender, RoutedEventArgs e) =>
        OpenGameProfilesWindow();

    private void OpenGameProfilesWindow()
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
        DiagnosticsNavButton.IsChecked = true;
    }

    private void DiagnosticsNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ShowDrawer(DrawerSection.Diagnostics, "Diagnostics");
        }
    }

    private void OpenDiagnosticsWindowButton_Click(object sender, RoutedEventArgs e) =>
        OpenDiagnosticsWindow();

    private void OpenDiagnosticsWindow()
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

    private void HomeNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        CloseDrawer();
        SetCaptureHeader();
    }

    private void MicrophoneNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ShowDrawer(DrawerSection.Microphone, "Microphone");
        }
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ShowDrawer(DrawerSection.Settings, "Settings");
        }
    }

    private void ShowDrawer(DrawerSection section, string title)
    {
        CurrentViewTitleText.Text = title;
        CurrentViewSubtitleText.Text = section switch
        {
            DrawerSection.Games => "Open taskbar windows and remembered profiles",
            DrawerSection.Microphone => "Routing and calibration",
            DrawerSection.Diagnostics => "Recovery, storage, and recent activity",
            DrawerSection.Settings => "Startup and background behavior",
            _ => string.Empty
        };
        DrawerTitleText.Text = title;
        GameDrawerContent.Visibility =
            section == DrawerSection.Games ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneDrawerContent.Visibility =
            section == DrawerSection.Microphone ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsDrawerContent.Visibility =
            section == DrawerSection.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
        SettingsDrawerContent.Visibility =
            section == DrawerSection.Settings ? Visibility.Visible : Visibility.Collapsed;

        DrawerScrim.Visibility = Visibility.Visible;
        DrawerPanel.Visibility = Visibility.Visible;
        var from = _drawerIsOpen ? DrawerTransform.X : 400;
        _drawerIsOpen = true;
        DrawerTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(from, 0, DrawerAnimationDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        DrawerScrim.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(DrawerScrim.Opacity, 1, DrawerAnimationDuration));
        DrawerCloseButton.Focus();
    }

    private void CloseDrawer()
    {
        if (!_drawerIsOpen)
        {
            return;
        }

        _drawerIsOpen = false;
        var slide = new DoubleAnimation(
            DrawerTransform.X,
            400,
            DrawerAnimationDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        slide.Completed += (_, _) =>
        {
            if (!_drawerIsOpen)
            {
                DrawerPanel.Visibility = Visibility.Collapsed;
                DrawerScrim.Visibility = Visibility.Collapsed;
            }
        };
        DrawerTransform.BeginAnimation(TranslateTransform.XProperty, slide);
        DrawerScrim.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(DrawerScrim.Opacity, 0, DrawerAnimationDuration));
    }

    private void DrawerCloseButton_Click(object sender, RoutedEventArgs e)
    {
        HomeNavButton.IsChecked = true;
        SetCaptureHeader();
        CloseDrawer();
    }

    private void DrawerScrim_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        HomeNavButton.IsChecked = true;
        SetCaptureHeader();
        CloseDrawer();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _drawerIsOpen)
        {
            HomeNavButton.IsChecked = true;
            SetCaptureHeader();
            CloseDrawer();
            e.Handled = true;
        }
    }

    private void SetCaptureHeader()
    {
        CurrentViewTitleText.Text = "Capture";
        CurrentViewSubtitleText.Text = "Continuous recording controls";
    }

    private async void ObsSetupButton_Click(object sender, RoutedEventArgs e) =>
        await RunObsSetupAsync();

    private async Task<bool> RunObsSetupAsync()
    {
        if (_isSetupBusy)
        {
            return _obsReady;
        }

        _isSetupBusy = true;
        SetupProgressBar.Visibility = Visibility.Visible;
        SetupProgressBar.IsIndeterminate = true;
        UpdateRecordingControls();

        var progress = new Progress<ObsSetupProgress>(update =>
        {
            SetStatus(update.Message);
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
            SetStatus(result.Message);
            ObsSetupButton.Content =
                result.IsSuccessful ? "Check OBS" : "Retry OBS setup";
            return result.IsSuccessful;
        }
        catch (Exception ex)
        {
            ReportCommandFailure("OBS setup", ex);
            return false;
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
            SetStatus("Protected previous 5 minutes");
        });
    }

    private void StartWithWindowsToggle_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isLoadingExperienceSettings)
        {
            return;
        }

        var enabled = StartWithWindowsToggle.IsChecked == true;
        bool? previousState = null;
        try
        {
            previousState = _windowsStartupManager.IsEnabled;
            _windowsStartupManager.SetEnabled(enabled);
            SaveExperienceSettings(
                _experienceSettingsStore.Current with { StartWithWindows = enabled });
            SetStatus(enabled
                ? "Blackbox will start with Windows"
                : "Windows startup disabled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not update Windows startup registration.");
            if (previousState is not null)
            {
                try
                {
                    _windowsStartupManager.SetEnabled(previousState.Value);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(
                        rollbackException,
                        "Could not restore the previous Windows startup registration.");
                }
            }

            SyncExperienceToggles();
            SetStatus($"Windows startup failed: {ex.Message}");
        }
    }

    private async void WatchGamesToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoadingExperienceSettings)
        {
            return;
        }

        var enabled = WatchGamesToggle.IsChecked == true;
        await ExecuteCommandAsync(
            "Update remembered-game watching",
            () => SetAutomaticCaptureAsync(enabled, persistPreference: true));
        SyncExperienceToggles();
    }

    private void CloseToTrayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoadingExperienceSettings)
        {
            return;
        }

        var enabled = CloseToTrayToggle.IsChecked == true;
        try
        {
            SaveExperienceSettings(
                _experienceSettingsStore.Current with { CloseToTray = enabled });
            SetStatus(enabled
                ? "Close button will keep Blackbox in the notification area"
                : "Close button will exit Blackbox");
        }
        catch (Exception ex)
        {
            SyncExperienceToggles();
            ReportCommandFailure("Update notification-area setting", ex);
        }
    }

    private void LoadExperienceSettings()
    {
        _isLoadingExperienceSettings = true;
        try
        {
            var settings = _experienceSettingsStore.Current;
            StartWithWindowsToggle.IsChecked =
                settings.StartWithWindows && _windowsStartupManager.IsEnabled;
            WatchGamesToggle.IsChecked = settings.WatchRememberedGames;
            CloseToTrayToggle.IsChecked = settings.CloseToTray;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Windows startup registration.");
            StartWithWindowsToggle.IsChecked =
                _experienceSettingsStore.Current.StartWithWindows;
            WatchGamesToggle.IsChecked =
                _experienceSettingsStore.Current.WatchRememberedGames;
            CloseToTrayToggle.IsChecked =
                _experienceSettingsStore.Current.CloseToTray;
        }
        finally
        {
            _isLoadingExperienceSettings = false;
        }
    }

    private void SaveExperienceSettings(UserExperienceSettings settings)
    {
        _experienceSettingsStore.Save(settings);
        SyncExperienceToggles();
    }

    private void SyncExperienceToggles()
    {
        _isLoadingExperienceSettings = true;
        try
        {
            var settings = _experienceSettingsStore.Current;
            StartWithWindowsToggle.IsChecked = settings.StartWithWindows;
            WatchGamesToggle.IsChecked = settings.WatchRememberedGames;
            CloseToTrayToggle.IsChecked = settings.CloseToTray;
        }
        finally
        {
            _isLoadingExperienceSettings = false;
        }
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

    private void ReportCommandFailure(
        string commandName,
        Exception exception)
    {
        _logger.LogError(exception, "{CommandName} failed.", commandName);
        SetStatus($"{commandName} failed: {exception.Message}");
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        DrawerSystemStatusText.Text = message;
    }

    private void AutomaticCaptureService_StatusChanged(
        AutomaticCaptureStatus status)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            AutomaticCaptureStatusText.Text = status.Message;
            DrawerAutomaticStatusText.Text = status.Message;
            var game = status.Target?.Title ?? "None detected";
            CurrentGameText.Text = game;
            DrawerGameText.Text = game;
            HeaderGameText.Text =
                status.Target?.Title.ToUpperInvariant() ?? "NO GAME";
            if (_automaticCaptureService.IsEnabled ||
                status.State == AutomaticCaptureState.Faulted)
            {
                SetStatus(status.Message);
            }
            else if (status.State == AutomaticCaptureState.Disabled &&
                !_coordinator.IsRecording)
            {
                SetStatus("Idle");
            }

            UpdateRecordingControls();
        });
    }

    private void UpdateRecordingControls()
    {
        var automatic = _automaticCaptureService.IsEnabled;
        var recording = _coordinator.IsRecording;
        var busy = _isSetupBusy || _isRecoveryBusy;
        AutoCaptureButton.Content = automatic ? "Disable auto" : "Enable auto";
        ObsSetupButton.IsEnabled = !busy && !automatic && !recording;
        AutoCaptureButton.IsEnabled = _obsReady && !busy;
        StartButton.IsEnabled = _obsReady && !busy && !automatic && !recording;
        StopButton.IsEnabled = recording;
        DiagnosticsButton.IsEnabled = !_isRecoveryBusy;
        AudioButton.IsEnabled = _obsReady && !busy;
        CalibrateButton.IsEnabled = _obsReady && !busy;
        DrawerApplyAudioButton.IsEnabled = _obsReady && !busy;
        DrawerCalibrateButton.IsEnabled = _obsReady && !busy;

        ObsStatusText.Text = _obsReady ? "OBS READY" : "OBS SETUP";
        ObsStatusDot.Fill = GetStatusBrush(_obsReady
            ? "SuccessBrush"
            : "WarningBrush");

        var sidebarState = recording
            ? ("Recording", "RecordingBrush")
            : automatic
                ? ("Watching games", "AccentBrush")
                : _obsReady
                    ? ("Ready", "SuccessBrush")
                    : ("OBS setup required", "WarningBrush");
        SidebarStatusText.Text = sidebarState.Item1;
        SidebarStatusDot.Fill = GetStatusBrush(sidebarState.Item2);

        SyncExperienceToggles();

        _trayIconService.Update(new TrayIconState(
            recording,
            automatic,
            _obsReady,
            CurrentGameText.Text == "None detected" ? null : CurrentGameText.Text));
    }

    private Brush GetStatusBrush(string resourceKey) =>
        (Brush)FindResource(resourceKey);

    private void TrayIconService_CommandRequested(TrayCommand command)
    {
        _ = Dispatcher.BeginInvoke(async () =>
        {
            switch (command)
            {
                case TrayCommand.Show:
                    ShowFromTray();
                    break;
                case TrayCommand.StartRecording:
                    await StartRecordingAsync();
                    break;
                case TrayCommand.StopRecording:
                    await StopRecordingAsync();
                    break;
                case TrayCommand.ProtectRecent:
                    await ProtectLastFiveMinutesAsync();
                    break;
                case TrayCommand.ToggleAutomaticCapture:
                    await ExecuteCommandAsync(
                        "Toggle automatic capture",
                        () => SetAutomaticCaptureAsync(
                            !_automaticCaptureService.IsEnabled,
                            persistPreference: true));
                    break;
                case TrayCommand.OpenRecordings:
                    ShowFromTray();
                    OpenRecordingLibraryWindow();
                    break;
                case TrayCommand.Exit:
                    _exitRequested = true;
                    Close();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command));
            }
        });
    }

    private void MainWindow_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized &&
            _experienceSettingsStore.Current.CloseToTray)
        {
            HideToTray(showNotification: false);
        }
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void HideToTray(bool showNotification)
    {
        ShowInTaskbar = false;
        Hide();
        if (showNotification)
        {
            _trayIconService.ShowBackgroundTip();
        }
    }

    protected override async void OnClosing(
        System.ComponentModel.CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_shutdownComplete)
        {
            base.OnClosing(e);
            return;
        }

        if (!_exitRequested &&
            _experienceSettingsStore.Current.CloseToTray)
        {
            e.Cancel = true;
            HideToTray(showNotification: true);
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
        _trayIconService.CommandRequested -= TrayIconService_CommandRequested;
        _automaticCaptureService.StatusChanged -=
            AutomaticCaptureService_StatusChanged;

        if (_automaticCaptureService.IsEnabled)
        {
            try
            {
                await _automaticCaptureService.SetEnabledAsync(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Could not stop automatic capture while Blackbox was closing.");
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
                _logger.LogError(
                    ex,
                    "Could not stop recording while Blackbox was closing.");
            }
        }

        _trayIconService.Dispose();
        _startupRecoveryCancellation.Dispose();
        _shutdownComplete = true;
        Close();
    }

    private enum DrawerSection
    {
        Games,
        Microphone,
        Diagnostics,
        Settings
    }
}
