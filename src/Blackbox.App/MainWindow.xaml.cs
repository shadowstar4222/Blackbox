using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Blackbox.App.Hotkeys;
using Blackbox.Domain;
using Blackbox.Export;
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
    private readonly RecordingCoordinator _coordinator;
    private readonly IObsController _obsController;
    private readonly AutomaticCaptureService _automaticCaptureService;
    private readonly RecordingGuardianService _recordingGuardian;
    private readonly MicrophoneDeviceMonitor _microphoneDeviceMonitor;
    private readonly ManualCaptureTargetResolver _manualTargetResolver;
    private readonly IRunningApplicationCatalog _applications;
    private readonly RecordingQualityContext _qualityContext;
    private readonly GameProfileSettingsService _profileSettings;
    private readonly IMicrophoneConfigurationStore _microphoneConfiguration;
    private readonly StartupRecoveryCoordinator _startupRecoveryCoordinator;
    private readonly AudioConfigurationService _audioConfigurationService;
    private readonly ProtectionService _protectionService;
    private readonly LiveMarkerService _liveMarkerService;
    private readonly InstantClipService _instantClipService;
    private readonly StorageQuotaEnforcer _storageQuotaEnforcer;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly RecordingSettings _settings;
    private readonly IClock _clock;
    private readonly UserExperienceSettingsStore _experienceSettingsStore;
    private readonly IWindowsStartupManager _windowsStartupManager;
    private readonly TrayIconService _trayIconService;
    private readonly Func<MicrophonePage> _microphonePageFactory;
    private readonly Func<RecordingLibraryPage> _recordingLibraryPageFactory;
    private readonly Func<GameProfilesPage> _gameProfilesPageFactory;
    private readonly Func<DiagnosticsPage> _diagnosticsPageFactory;
    private readonly ILogger<MainWindow> _logger;
    private readonly CancellationTokenSource _startupRecoveryCancellation = new();
    private RecordingLibraryPage? _recordingLibraryPage;
    private GameProfilesPage? _gameProfilesPage;
    private MicrophonePage? _microphonePage;
    private DiagnosticsPage? _diagnosticsPage;
    private bool _recorderReady;
    private bool _isSetupBusy;
    private bool _isRecordingCommandBusy;
    private bool _isAutomaticCaptureCommandBusy;
    private string? _recordingFailure;
    private bool _isRecoveryBusy = true;
    private bool _isShutdownInProgress;
    private bool _shutdownComplete;
    private bool _exitRequested;
    private bool _isLoadingExperienceSettings;
    private bool _isSelectingNavigation;
    private RecordingGuardianState _lastGuardianState = RecordingGuardianState.Stopped;
    private string? _lastCaptureNotificationIdentity;
    private DateTimeOffset? _lastStorageCheckAt;
    private bool _lowStorageReported;
    private byte[]? _lastGuardianPreviewBytes;
    private IReadOnlyList<WorkspaceCommand> _workspaceCommands = [];

    public MainWindow(
        RecordingCoordinator coordinator,
        IObsController obsController,
        AutomaticCaptureService automaticCaptureService,
        RecordingGuardianService recordingGuardian,
        MicrophoneDeviceMonitor microphoneDeviceMonitor,
        ManualCaptureTargetResolver manualTargetResolver,
        IRunningApplicationCatalog applications,
        RecordingQualityContext qualityContext,
        GameProfileSettingsService profileSettings,
        IMicrophoneConfigurationStore microphoneConfiguration,
        StartupRecoveryCoordinator startupRecoveryCoordinator,
        AudioConfigurationService audioConfigurationService,
        ProtectionService protectionService,
        LiveMarkerService liveMarkerService,
        InstantClipService instantClipService,
        StorageQuotaEnforcer storageQuotaEnforcer,
        GlobalHotkeyService hotkeyService,
        RecordingSettings settings,
        IClock clock,
        UserExperienceSettingsStore experienceSettingsStore,
        IWindowsStartupManager windowsStartupManager,
        TrayIconService trayIconService,
        Func<MicrophonePage> microphonePageFactory,
        Func<RecordingLibraryPage> recordingLibraryPageFactory,
        Func<GameProfilesPage> gameProfilesPageFactory,
        Func<DiagnosticsPage> diagnosticsPageFactory,
        ILogger<MainWindow> logger)
    {
        _coordinator = coordinator;
        _obsController = obsController;
        _automaticCaptureService = automaticCaptureService;
        _recordingGuardian = recordingGuardian;
        _microphoneDeviceMonitor = microphoneDeviceMonitor;
        _manualTargetResolver = manualTargetResolver;
        _applications = applications;
        _qualityContext = qualityContext;
        _profileSettings = profileSettings;
        _microphoneConfiguration = microphoneConfiguration;
        _startupRecoveryCoordinator = startupRecoveryCoordinator;
        _audioConfigurationService = audioConfigurationService;
        _protectionService = protectionService;
        _liveMarkerService = liveMarkerService;
        _instantClipService = instantClipService;
        _storageQuotaEnforcer = storageQuotaEnforcer;
        _hotkeyService = hotkeyService;
        _settings = settings;
        _clock = clock;
        _experienceSettingsStore = experienceSettingsStore;
        _windowsStartupManager = windowsStartupManager;
        _trayIconService = trayIconService;
        _microphonePageFactory = microphonePageFactory;
        _recordingLibraryPageFactory = recordingLibraryPageFactory;
        _gameProfilesPageFactory = gameProfilesPageFactory;
        _diagnosticsPageFactory = diagnosticsPageFactory;
        _logger = logger;

        InitializeComponent();
        InitializeCaptureTelemetry();
        InitializeWorkspaceCommands();
        if (_obsController is INativeRecorderController native)
        {
            native.RecordingFailed += NativeRecorder_RecordingFailed;
        }
        InitializeQualityOptions();
        _coordinator.RecordingStateChanged += RecordingCoordinator_RecordingStateChanged;
        _automaticCaptureService.StatusChanged += AutomaticCaptureService_StatusChanged;
        _recordingGuardian.SnapshotChanged += RecordingGuardian_SnapshotChanged;
        _microphoneDeviceMonitor.StatusChanged += MicrophoneDeviceMonitor_StatusChanged;
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
            SetStatus(message);
        });
        try
        {
            var outcome = await _startupRecoveryCoordinator.RunAsync(
                progress,
                _startupRecoveryCancellation.Token);
            _recorderReady = outcome.RecorderReady;
            SetStatus(!outcome.RecorderReady || outcome.RecordingAdopted ||
                outcome.RecordingRecovery.DamagedFiles > 0 || outcome.RecordingRecovery.RecoveredFiles > 0
                ? outcome.Message : null);
            ObsSetupButton.Content = outcome.RecorderReady ? "Check recorder" : "Retry recorder check";

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
            await _recordingGuardian.StartAsync();
            UpdateRecordingControls();
            if (!StartHidden && !_experienceSettingsStore.Current.HasCompletedTutorial)
            {
                HelpNavButton.IsChecked = true;
            }
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
        await ExecuteCommandAsync("Start recording", () => StartRecordingCoreAsync());
    }

    private async Task StartRecordingCoreAsync(CancellationToken cancellationToken = default)
    {
        if (_isSetupBusy || _openingRecordingSetup || _isRecoveryBusy || _isRecordingCommandBusy || _isAutomaticCaptureCommandBusy || _coordinator.IsTransitioning ||
            _isShutdownInProgress || _automaticCaptureService.IsEnabled || _coordinator.IsRecording)
        {
            return;
        }

        _isRecordingCommandBusy = true;
        UpdateRecordingControls();
        try
        {
            var target = await _manualTargetResolver.ResolveAsync(cancellationToken);
            if (target is null)
            {
                ShowFromTray();
                await SelectNavigationAsync(GamesNavButton, OpenGameProfilesPageAsync);
                SetStatus("Recording not started. In Games, remember a running application and choose Use for capture. Its window must be open and not minimized.");
                return;
            }

            var manualSettings = _settings with
            {
                RecordingLocation = RecordingDirectoryLayout.GetSessionDirectory(
                    _settings.RecordingLocation,
                    target.Title,
                    _clock.UtcNow)
            };
            _recordingFailure = null;
            if (_manualTargetResolver.TemporaryTarget?.ProcessId == target.ProcessId)
            {
                Directory.CreateDirectory(manualSettings.RecordingLocation);
                File.WriteAllText(Path.Combine(manualSettings.RecordingLocation, "PHOTOSENSITIVITY-WARNING.txt"), RecordingSetupProtocol.Warning);
            }
            var effective = await _profileSettings.ResolveAsync(target, cancellationToken);
            if (_validatingSetup && _manualTargetResolver.TemporaryTarget is not null)
                effective = effective with { CaptureMicrophone = false };
            if (await _coordinator.TryStartAsync(manualSettings, target, effective, cancellationToken))
            {
                _recorderReady = true;
                SetStatus(null);
            }
            else
            {
                SetStatus("A recording is already active; no new session was started.");
            }
        }
        finally
        {
            _isRecordingCommandBusy = false;
            UpdateRecordingControls();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) =>
        await StopRecordingAsync();

    private async Task StopRecordingAsync()
    {
        await ExecuteCommandAsync("Stop recording", () => StopRecordingCoreAsync());
    }

    private async Task StopRecordingCoreAsync(CancellationToken cancellationToken = default)
    {
        _setupAutoRecordArmed = false;
        _setupAutoRecording = false;
        if (_automaticCaptureService.IsEnabled)
        {
            await SetAutomaticCaptureAsync(
                false,
                persistPreference: true,
                cancellationToken);
        }

        if (_coordinator.IsRecording)
        {
            await _coordinator.StopAsync(cancellationToken);
        }

        SetStatus(null);
        UpdateRecordingControls();
    }

    private async void AutoCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync(
            "Toggle automatic capture",
            () => SetAutomaticCaptureAsync(
                !IsAutomaticRecordingEnabled,
                persistPreference: true));
    }

    private async Task SetAutomaticCaptureAsync(
        bool enabled,
        bool persistPreference,
        CancellationToken cancellationToken = default)
    {
        if (_isAutomaticCaptureCommandBusy) return;
        _isAutomaticCaptureCommandBusy = true;
        UpdateRecordingControls();
        try { await SetAutomaticCaptureCoreAsync(enabled, persistPreference, cancellationToken); }
        finally { _isAutomaticCaptureCommandBusy = false; UpdateRecordingControls(); }
    }

    private async Task SetAutomaticCaptureCoreAsync(
        bool enabled,
        bool persistPreference,
        CancellationToken cancellationToken)
    {
        if (enabled && !_recorderReady && !await CheckRecorderAsync())
        {
            UpdateRecordingControls();
            return;
        }

        var previousState = IsAutomaticRecordingEnabled;
        await ApplyAutomaticCaptureModeAsync(enabled, cancellationToken);
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
                if (IsAutomaticRecordingEnabled != previousState)
                {
                    await ApplyAutomaticCaptureModeAsync(
                        previousState,
                        CancellationToken.None);
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

    private async Task ApplyAutomaticCaptureModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_setupProcess is null)
        {
            await _automaticCaptureService.SetEnabledAsync(enabled, cancellationToken);
            return;
        }
        var changed = enabled != _setupRestoresAutomatic;
        _setupRestoresAutomatic = enabled;
        if (enabled && changed) _setupAutoRecordArmed = !_coordinator.IsRecording;
        if (!enabled)
        {
            _setupAutoRecordArmed = false;
            if (_setupAutoRecording && _coordinator.IsRecording)
                await _coordinator.TryStopAsync(cancellationToken);
            _setupAutoRecording = false;
        }
        _setupMessage = _coordinator.IsRecording ? "Recording this window. Stop to save the test."
            : enabled ? "Automatic recording will start when the scene is ready."
            : "Automatic recording is off. Press Record to save a test.";
    }

    private async Task EnsureAutomaticCaptureReadyAsync()
    {
        if (!_recorderReady && !await CheckRecorderAsync())
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

    private async void SaveClipButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteCommandAsync("Save instant clip", SaveInstantClipAsync);

    private async Task SaveInstantClipAsync()
    {
        if (!_coordinator.IsRecording)
            throw new InvalidOperationException("Start recording before saving an instant clip.");
        var duration = InstantClipDurationParser.Parse(ClipDurationComboBox.Text);
        await _instantClipService.CreateAsync(duration);
        SetStatus($"Queued previous {FormatClipDuration(duration)} as an instant clip");
    }

    private async void GuardianCheckButton_Click(object sender, RoutedEventArgs e)
    {
        GuardianCheckButton.IsEnabled = false;
        try
        {
            await _recordingGuardian.CheckNowAsync();
        }
        catch (Exception ex)
        {
            ReportCommandFailure("Recording Guardian check", ex);
        }
        finally
        {
            GuardianCheckButton.IsEnabled = true;
        }
    }

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

    private async void RecordingLibraryButton_Click(object sender, RoutedEventArgs e)
        => await SelectNavigationAsync(
            RecordingsNavButton,
            OpenRecordingLibraryPageAsync);

    private async void RecordingNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isSelectingNavigation)
        {
            return;
        }

        await OpenRecordingLibraryPageAsync();
    }

    private async Task OpenRecordingLibraryPageAsync()
    {
        try
        {
            _recordingLibraryPage ??= _recordingLibraryPageFactory();
            ShowWorkspacePage(
                _recordingLibraryPage,
                "Recordings");
            await _recordingLibraryPage.ActivateAsync();
        }
        catch (Exception ex)
        {
            ReportCommandFailure("Open recordings", ex);
        }
    }

    private async void GamesButton_Click(object sender, RoutedEventArgs e) =>
        await SelectNavigationAsync(
            GamesNavButton,
            OpenGameProfilesPageAsync);

    private async void GamesNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_isSelectingNavigation)
        {
            await OpenGameProfilesPageAsync();
        }
    }

    private async Task OpenGameProfilesPageAsync()
    {
        try
        {
            _gameProfilesPage ??= _gameProfilesPageFactory();
            ShowWorkspacePage(
                _gameProfilesPage,
                "Games");
            await _gameProfilesPage.ActivateAsync();
        }
        catch (Exception ex)
        {
            ReportCommandFailure("Open games", ex);
        }
    }

    private async void DiagnosticsNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_isSelectingNavigation)
        {
            await OpenDiagnosticsPageAsync();
        }
    }

    private async Task OpenDiagnosticsPageAsync()
    {
        try
        {
            _diagnosticsPage ??= _diagnosticsPageFactory();
            _diagnosticsPage.OpenRecordingSetupAsync = OpenRecordingSetupAsync;
            ShowWorkspacePage(
                _diagnosticsPage,
                "Diagnostics");
            await _diagnosticsPage.ActivateAsync();
        }
        catch (Exception ex)
        {
            ReportCommandFailure("Open diagnostics", ex);
        }
    }

    private void HomeNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isSelectingNavigation)
        {
            return;
        }

        SetCaptureHeader();
    }

    private async void MicrophoneNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_isSelectingNavigation)
        {
            await OpenMicrophonePageAsync();
        }
    }

    private async Task OpenMicrophonePageAsync()
    {
        try
        {
            _microphonePage ??= _microphonePageFactory();
            ShowWorkspacePage(
                _microphonePage,
                "Microphone");
            await _microphonePage.ActivateAsync();
        }
        catch (Exception ex)
        {
            ReportCommandFailure("Open microphone setup", ex);
        }
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_isSelectingNavigation)
        {
            ShowInlinePage(InlinePageSection.Settings, "Settings");
        }
    }

    private void HelpNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_isSelectingNavigation)
        {
            ShowInlinePage(InlinePageSection.Help, "Help");
        }
    }

    private async void TutorialSetupObsButton_Click(object sender, RoutedEventArgs e)
    {
        HomeNavButton.IsChecked = true;
        CloseInlinePage();
        await CheckRecorderAsync();
    }

    private async void TutorialGamesButton_Click(object sender, RoutedEventArgs e) =>
        await SelectNavigationAsync(
            GamesNavButton,
            OpenGameProfilesPageAsync);

    private async void TutorialMicrophoneButton_Click(object sender, RoutedEventArgs e) =>
        await SelectNavigationAsync(
            MicrophoneNavButton,
            OpenMicrophonePageAsync);

    private void CompleteTutorialButton_Click(object sender, RoutedEventArgs e)
    {
        SaveExperienceSettings(
            _experienceSettingsStore.Current with { HasCompletedTutorial = true });
        SetStatus("Tutorial complete");
        HomeNavButton.IsChecked = true;
        SetCaptureHeader();
    }

    private void ShowInlinePage(InlinePageSection section, string title)
    {
        HideWorkspacePages();
        CapturePage.Visibility = Visibility.Collapsed;
        WorkspacePageHost.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        CurrentViewTitleText.Text = title;
        HelpPageContent.Visibility =
            section == InlinePageSection.Help ? Visibility.Visible : Visibility.Collapsed;
        SettingsPageContent.Visibility =
            section == InlinePageSection.Settings ? Visibility.Visible : Visibility.Collapsed;

        InlinePagePanel.Visibility = Visibility.Visible;
        BackButton.Focus();
    }

    private void CloseInlinePage()
    {
        InlinePagePanel.Visibility = Visibility.Collapsed;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingLibraryPage?.IsVisible == true && _recordingLibraryPage.TryClosePlayback()) return;
        HomeNavButton.IsChecked = true;
        SetCaptureHeader();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && CommandPaletteOverlay.Visibility == Visibility.Visible)
        {
            CloseCommandPalette();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && BackButton.Visibility == Visibility.Visible)
        {
            BackButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void InitializeWorkspaceCommands()
    {
        _workspaceCommands =
        [
            new("Recordings", "Review, tag, and export sessions", "\uE8B2", () => SelectNavigationAsync(RecordingsNavButton, OpenRecordingLibraryPageAsync)),
            new("Time-shift review", "Review finalized footage while capture continues", "\uE823", async () =>
            {
                await SelectNavigationAsync(RecordingsNavButton, OpenRecordingLibraryPageAsync);
                if (_recordingLibraryPage is not null) await _recordingLibraryPage.OpenTimeShiftAsync();
            }, () => _coordinator.IsRecording),
            new("Choose window", "Remember an application or switch capture", "\uE7F4", () => SelectNavigationAsync(GamesNavButton, OpenGameProfilesPageAsync)),
            new("Microphone", "Routing, voice presets, and calibration", "\uE720", () => SelectNavigationAsync(MicrophoneNavButton, OpenMicrophonePageAsync)),
            new("Start recording", "Record the selected application", "\uE7C8", StartRecordingAsync, () => StartButton.IsEnabled),
            new("Stop recording", "Finalize the current session", "\uE71A", StopRecordingAsync, () => StopButton.IsEnabled),
            new("Save recent clip", "Use the clip duration in the recording strip", "\uE714", SaveInstantClipAsync, () => SaveClipButton.IsEnabled),
            new("Mark this moment", "Add a manual event to the timeline", "\uE735", AddMomentAsync, () => AddMomentButton.IsEnabled),
            new("Protect previous 5 minutes", "Keep recent footage from automatic cleanup", "\uE72E", ProtectLastFiveMinutesAsync),
            new("Automatic capture", "Toggle recording for remembered applications", "\uE768", () => SetAutomaticCaptureAsync(!IsAutomaticRecordingEnabled, true), () => AutoCaptureButton.IsEnabled),
            new("Diagnostics", "Capture health, storage, and recovery", "\uE9D9", () => SelectNavigationAsync(DiagnosticsNavButton, OpenDiagnosticsPageAsync)),
            new("Settings", "Recording quality and startup preferences", "\uE713", () => { SettingsNavButton.IsChecked = true; return Task.CompletedTask; }),
            new("Help", "Tutorial and control reference", "\uE897", () => { HelpNavButton.IsChecked = true; return Task.CompletedTask; }),
            new("Open recording folder", "Open local footage in File Explorer", "\uE8B7", () => { OpenRecordingsButton_Click(this, new RoutedEventArgs()); return Task.CompletedTask; })
        ];
    }

    private void OpenCommandPalette_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();
    private void CloseCommandPalette_Click(object sender, RoutedEventArgs e) => CloseCommandPalette();

    private void OpenCommandPalette()
    {
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        _recordingLibraryPage?.SetReviewObscured(true);
        CommandSearchBox.Clear();
        RefreshCommandResults();
        FocusManager.SetFocusedElement(CommandPaletteOverlay, CommandSearchBox);
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (CommandPaletteOverlay.Visibility == Visibility.Visible)
            {
                CommandSearchBox.Focus();
                Keyboard.Focus(CommandSearchBox);
            }
        });
    }

    private void CloseCommandPalette()
    {
        CommandPaletteOverlay.Visibility = Visibility.Collapsed;
        _recordingLibraryPage?.SetReviewObscured(false);
        HomeNavButton.Focus();
    }

    private void CommandPaletteOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, CommandPaletteOverlay)) CloseCommandPalette();
    }

    private void CommandSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CommandResultsList is not null) RefreshCommandResults();
    }

    private void RefreshCommandResults()
    {
        var query = CommandSearchBox.Text.Trim();
        CommandResultsList.ItemsSource = _workspaceCommands.Where(command =>
            (command.Available?.Invoke() ?? true) &&
            (command.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             command.Detail.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        CommandResultsList.SelectedIndex = CommandResultsList.Items.Count > 0 ? 0 : -1;
        CommandNoResultsText.Visibility = CommandResultsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CommandPalette_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            if (CommandResultsList.Items.Count == 0) { e.Handled = true; return; }
            var direction = e.Key == Key.Down ? 1 : -1;
            CommandResultsList.SelectedIndex = Math.Clamp(CommandResultsList.SelectedIndex + direction, 0, Math.Max(0, CommandResultsList.Items.Count - 1));
            CommandResultsList.ScrollIntoView(CommandResultsList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ExecuteSelectedWorkspaceCommandAsync();
        }
    }

    private async void CommandResultsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(CommandResultsList, source) is ListBoxItem)
            await ExecuteSelectedWorkspaceCommandAsync();
    }

    private async Task ExecuteSelectedWorkspaceCommandAsync()
    {
        if (CommandResultsList.SelectedItem is not WorkspaceCommand command ||
            !(command.Available?.Invoke() ?? true)) return;
        CloseCommandPalette();
        await ExecuteCommandAsync(command.Name, command.Execute);
    }

    private async void AddMomentButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteCommandAsync("Mark this moment", AddMomentAsync);

    private async Task AddMomentAsync()
    {
        await _liveMarkerService.AddAsync();
        SetStatus("Moment marked");
    }

    private async Task SelectNavigationAsync(
        RadioButton navigationButton,
        Func<Task> navigation)
    {
        _isSelectingNavigation = true;
        try
        {
            navigationButton.IsChecked = true;
        }
        finally
        {
            _isSelectingNavigation = false;
        }

        await navigation();
    }

    private void ShowWorkspacePage(
        FrameworkElement page,
        string title)
    {
        CloseInlinePage();
        HideWorkspacePages();
        if (!WorkspacePageHost.Children.Contains(page))
        {
            WorkspacePageHost.Children.Add(page);
        }

        page.Visibility = Visibility.Visible;
        CapturePage.Visibility = Visibility.Collapsed;
        WorkspacePageHost.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        CurrentViewTitleText.Text = title;
        BackButton.Focus();
    }

    private void HideWorkspacePages()
    {
        foreach (UIElement child in WorkspacePageHost.Children)
        {
            child.Visibility = Visibility.Collapsed;
        }
    }

    private void SetCaptureHeader()
    {
        CloseInlinePage();
        HideWorkspacePages();
        WorkspacePageHost.Visibility = Visibility.Collapsed;
        CapturePage.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        CurrentViewTitleText.Text = "Capture";
    }

    private async void ObsSetupButton_Click(object sender, RoutedEventArgs e) =>
        await CheckRecorderAsync();

    private async Task<bool> CheckRecorderAsync()
    {
        if (_isSetupBusy)
        {
            return _recorderReady;
        }

        _isSetupBusy = true;
        SetupProgressBar.Visibility = Visibility.Visible;
        SetupProgressBar.IsIndeterminate = true;
        UpdateRecordingControls();

        try
        {
            SetStatus(null);
            await _obsController.LaunchAsync(_startupRecoveryCancellation.Token);
            _recorderReady = true;
            SetStatus(null);
            ObsSetupButton.Content = "Check recorder";
            return true;
        }
        catch (Exception ex)
        {
            _recorderReady = false;
            ObsSetupButton.Content = "Retry recorder check";
            ReportCommandFailure("Recorder check", ex);
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
        await ExecuteCommandAsync(
            "Protect previous 5 minutes",
            () => ProtectLastFiveMinutesCoreAsync());
    }

    private async Task ProtectLastFiveMinutesCoreAsync(
        CancellationToken cancellationToken = default)
    {
        await _protectionService.ProtectPreviousFiveMinutesAsync(cancellationToken);
        SetStatus("Protected previous 5 minutes");
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

    private async void ApplyQualityButton_Click(object sender, RoutedEventArgs e)
    {
        if (ResolutionComboBox.SelectedValue is not RecordingResolution resolution ||
            FrameRateComboBox.SelectedValue is not int framesPerSecond ||
            AudioQualityComboBox.SelectedValue is not int audioBitrateKbps)
        {
            SetStatus("Choose a resolution, frame rate, and audio quality");
            return;
        }

        await ExecuteCommandAsync("Apply recording quality", async () =>
        {
            var quality = new RecordingQualitySettings
            {
                Resolution = resolution,
                FramesPerSecond = framesPerSecond,
                AudioBitrateKbps = audioBitrateKbps
            };
            quality.Validate();
            SaveExperienceSettings(
                _experienceSettingsStore.Current with { RecordingQuality = quality });
            await CheckRecorderAsync();
        });
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

    private void InitializeQualityOptions()
    {
        ResolutionComboBox.ItemsSource = new[]
        {
            new QualityOption<RecordingResolution>("Match application", RecordingResolution.MatchApplication),
            new QualityOption<RecordingResolution>("1280 x 720", RecordingResolution.Hd720),
            new QualityOption<RecordingResolution>("1920 x 1080", RecordingResolution.FullHd1080),
            new QualityOption<RecordingResolution>("2560 x 1440", RecordingResolution.QuadHd1440),
            new QualityOption<RecordingResolution>("3840 x 2160", RecordingResolution.UltraHd2160)
        };
        FrameRateComboBox.ItemsSource = new[]
        {
            new QualityOption<int>("30 FPS", 30),
            new QualityOption<int>("60 FPS", 60),
            new QualityOption<int>("120 FPS", 120)
        };
        AudioQualityComboBox.ItemsSource = new[]
        {
            new QualityOption<int>("Standard - 160 kbps", 160),
            new QualityOption<int>("High - 192 kbps", 192)
        };
    }

    private void SyncQualitySelections(RecordingQualitySettings? quality)
    {
        quality = (quality ?? new RecordingQualitySettings()).ForNativeRecording();
        ResolutionComboBox.SelectedValue = quality.Resolution;
        FrameRateComboBox.SelectedValue = quality.FramesPerSecond;
        AudioQualityComboBox.SelectedValue = quality.AudioBitrateKbps;
        QualitySummaryText.Text = $"{quality.Resolution} / {quality.FramesPerSecond} FPS / {quality.AudioBitrateKbps} kbps";
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
            SyncQualitySelections(settings.RecordingQuality);
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
            SyncQualitySelections(_experienceSettingsStore.Current.RecordingQuality);
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
            SyncQualitySelections(settings.RecordingQuality);
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

    private void SetStatus(string? message)
    {
        StatusText.Text = message is null ? string.Empty : GetRecorderMessage(message);
        StatusText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private string GetRecorderMessage(string message) =>
        _obsController is INativeRecorderController
            ? message.Replace("OBS", "recording engine", StringComparison.Ordinal)
            : message;

    private void AutomaticCaptureService_StatusChanged(
        AutomaticCaptureStatus status)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (status.State == AutomaticCaptureState.Faulted)
            {
                SetStatus(status.Message);
            }

            if (status.State == AutomaticCaptureState.Recording && status.Target is not null &&
                !string.Equals(
                    status.Target.CaptureBindingIdentity,
                    _lastCaptureNotificationIdentity,
                    StringComparison.Ordinal))
            {
                var switched = _lastCaptureNotificationIdentity is not null;
                _lastCaptureNotificationIdentity = status.Target.CaptureBindingIdentity;
                _trayIconService.ShowNotification(
                    switched ? "Blackbox switched capture" : "Blackbox capture started",
                    $"Now capturing {status.Target.Title} at {status.Target.WindowWidth} x {status.Target.WindowHeight}.");
            }
            else if (status.Target is null &&
                     status.State is AutomaticCaptureState.Watching or AutomaticCaptureState.Disabled)
            {
                _lastCaptureNotificationIdentity = null;
            }

            UpdateRecordingControls();
        });
    }

    private void RecordingGuardian_SnapshotChanged(RecordingGuardianSnapshot snapshot)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            var previousState = _lastGuardianState;
            _lastGuardianState = snapshot.State;
            UpdateGuardianDetails(snapshot);

            if (snapshot.State is RecordingGuardianState.Faulted or RecordingGuardianState.Repairing &&
                snapshot.State != previousState)
            {
                _trayIconService.ShowNotification(
                    snapshot.State == RecordingGuardianState.Faulted
                        ? "Blackbox capture needs attention"
                        : "Blackbox is repairing capture",
                    GetRecorderMessage(snapshot.Message),
                    snapshot.State == RecordingGuardianState.Faulted
                        ? TrayNotificationKind.Error
                        : TrayNotificationKind.Warning);
            }
            else if (snapshot.State == RecordingGuardianState.Healthy &&
                     previousState is RecordingGuardianState.Warning
                         or RecordingGuardianState.Repairing
                         or RecordingGuardianState.Faulted)
            {
                _trayIconService.ShowNotification(
                    "Blackbox capture recovered",
                    GetRecorderMessage(snapshot.Message));
            }

            CheckLowStorage();
        });
    }

    private void UpdateGuardianDetails(RecordingGuardianSnapshot snapshot)
    {
        CaptureHealthSummaryText.Text = snapshot.State switch
        {
            RecordingGuardianState.Healthy when snapshot.RecordingOutputActive => "Video and output verified",
            RecordingGuardianState.Healthy => "Window capture available",
            RecordingGuardianState.Checking => "Checking video and output...",
            RecordingGuardianState.Warning => "Capture needs attention",
            RecordingGuardianState.Repairing => "Repairing capture",
            RecordingGuardianState.Faulted => "Capture check failed",
            _ => snapshot.RecordingOutputActive ? "Output active; awaiting video check" : "No capture to check"
        };
        CaptureHealthSummaryText.Foreground = snapshot.State == RecordingGuardianState.Healthy
            ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("TextPrimaryBrush");
        CaptureHealthSummaryText.ToolTip = $"Last check: {snapshot.CapturedAt.ToLocalTime():T}";
        GuardianMessageText.Text = GetRecorderMessage(snapshot.Message);
        GuardianMessageText.Visibility = snapshot.State is RecordingGuardianState.Warning or
            RecordingGuardianState.Repairing or RecordingGuardianState.Faulted || snapshot.Issue != RecordingGuardianIssue.None
            ? Visibility.Visible : Visibility.Collapsed;
        GuardianCheckButton.ToolTip = $"Check capture health ({snapshot.State})";
        GuardianVideoText.Text = snapshot.OutputWidth > 0 && snapshot.OutputHeight > 0
            ? $"{snapshot.OutputWidth} x {snapshot.OutputHeight} | {snapshot.FramesPerSecond:0.##} FPS"
            : string.Empty;
        GuardianVideoText.Visibility = string.IsNullOrEmpty(GuardianVideoText.Text) ? Visibility.Collapsed : Visibility.Visible;
        GuardianEvidenceText.Text = string.IsNullOrWhiteSpace(snapshot.Evidence)
            ? string.Empty : GetRecorderMessage(snapshot.Evidence);
        GuardianAudioMeter.Value = GetAudioMeterValue(snapshot.GameAudioPeakDb);
        GuardianAudioMeter.IsEnabled = snapshot.GameAudioPeakDb is not null;
        GuardianAudioMeterRow.Visibility = snapshot.GameAudioPeakDb is null
            ? Visibility.Collapsed : Visibility.Visible;
        var mix = _obsController is INativeRecorderController && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)
            ? "System mix" : "Application mix";
        AudioSignalText.Text = snapshot.GameAudioPeakDb is { } peak
            ? $"{mix}  |  {(peak <= -60 ? "Quiet" : $"{peak:0.0} dBFS")}"
            : snapshot.RecordingOutputActive ? $"{mix}: no level received" : "Levels available during capture";
        MicrophoneSignalText.Text = !snapshot.MicrophoneCaptureEnabled ? "Microphone off"
            : snapshot.MicrophonePacketReceived ? "Microphone receiving audio" : "Microphone waiting for audio";
        MicrophoneSignalText.Visibility = snapshot.RecordingOutputActive && _obsController is INativeRecorderController
            ? Visibility.Visible : Visibility.Collapsed;

        var previewBytes = snapshot.Preview?.ImageBytes;
        if (!ReferenceEquals(previewBytes, _lastGuardianPreviewBytes))
        {
            _lastGuardianPreviewBytes = previewBytes;
            GuardianPreviewImage.Source = previewBytes is null ? null : CreatePreviewBitmap(previewBytes);
            GuardianPreviewPlaceholder.Visibility = previewBytes is null
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void NativeRecorder_RecordingFailed(string message)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_isShutdownInProgress)
            {
                return;
            }

            _recordingFailure = message;
            _recorderReady = false;
            _logger.LogError("Native recording failed: {RecordingFailure}", message);
            try
            {
                if (_automaticCaptureService.IsEnabled)
                {
                    await _automaticCaptureService.SetEnabledAsync(false);
                }

                await _coordinator.TryStopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not finalize the failed native recording.");
            }

            SetStatus($"Recording failed: {message}. Check the recorder before restarting.");
            _trayIconService.ShowNotification("Blackbox recording failed", message, TrayNotificationKind.Error);
            UpdateRecordingControls();
        });
    }

    private void RecordingCoordinator_RecordingStateChanged(bool isRecording)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (isRecording)
            {
                _recordingFailure = null;
                _recorderReady = true;
            }

            if (_recordingFailure is null)
            {
                _trayIconService.ShowNotification(
                isRecording ? "Blackbox recording started" : "Blackbox recording stopped",
                isRecording
                    ? "The selected window is being written to recording segments."
                    : "The recording session was closed cleanly.");
            }
            if (isRecording)
            {
                _lastStorageCheckAt = null;
                CheckLowStorage();
            }
            UpdateRecordingControls();
        });
    }

    private void MicrophoneDeviceMonitor_StatusChanged(MicrophoneDeviceStatus status)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            var disconnected = status.State == MicrophoneConnectionState.Disconnected;
            _trayIconService.ShowNotification(
                disconnected ? "Blackbox microphone disconnected" : "Blackbox microphone updated",
                disconnected
                    ? "The microphone is unavailable. Check the recording and reconnect or select a microphone."
                    : "Blackbox restored the active microphone routing.",
                disconnected ? TrayNotificationKind.Warning : TrayNotificationKind.Information);
        });
    }

    private void CheckLowStorage()
    {
        if (!_coordinator.IsRecording ||
            _lastStorageCheckAt is not null &&
            _clock.UtcNow - _lastStorageCheckAt < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastStorageCheckAt = _clock.UtcNow;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_settings.RecordingLocation));
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var freeBytes = new DriveInfo(root).AvailableFreeSpace;
            var minimumBytes = (long)(_settings.MinimumRemainingFreeDiskSpaceGigabytes *
                                      1024 * 1024 * 1024);
            if (freeBytes < minimumBytes && !_lowStorageReported)
            {
                _lowStorageReported = true;
                _trayIconService.ShowNotification(
                    "Blackbox storage is low",
                    $"Only {FormatGigabytes(freeBytes):0.0} GB is free on {root}. Storage cleanup may remove old unprotected segments.",
                    TrayNotificationKind.Warning);
            }
            else if (freeBytes >= minimumBytes)
            {
                _lowStorageReported = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not check free recording storage.");
        }
    }

    private static double FormatGigabytes(long bytes) =>
        bytes / (1024d * 1024d * 1024d);

    private static string FormatClipDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes:0.##} min"
            : $"{duration.TotalSeconds:0.##} sec";

    private static BitmapImage CreatePreviewBitmap(byte[] imageBytes)
    {
        using var stream = new MemoryStream(imageBytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static double GetAudioMeterValue(double? peakDb)
    {
        if (peakDb is null || double.IsNegativeInfinity(peakDb.Value))
        {
            return 0;
        }

        return Math.Clamp(peakDb.Value + 60, 0, 60);
    }

    private void UpdateRecordingControls()
    {
        UpdateAudioMeterTimer();
        var automatic = IsAutomaticRecordingEnabled;
        var recording = _coordinator.IsRecording && _recordingFailure is null;
        var busy = _isSetupBusy || _isRecoveryBusy || _isRecordingCommandBusy || _isAutomaticCaptureCommandBusy || _openingRecordingSetup ||
            _coordinator.IsTransitioning || _isShutdownInProgress;
        var target = _manualTargetResolver.TemporaryTarget ?? (_obsController as INativeRecorderController)?.Target ?? _automaticCaptureService.Status.Target;
        CurrentGameText.Text = target?.Title ?? "No window selected";
        AutoCaptureButton.Content = automatic ? "Disable auto" : "Enable auto";
        ObsSetupButton.IsEnabled = !busy && !automatic && !recording;
        AutoCaptureButton.IsEnabled = !busy;
        AutoCaptureButton.ToolTip = _setupProcess is not null
            ? automatic ? "Disable automatic recording for setup" : "Record setup automatically when the scene is ready"
            : automatic ? "Stop watching remembered applications" : "Watch remembered applications";
        WatchGamesToggle.IsEnabled = !busy;
        StartButton.IsEnabled = _recorderReady && !busy && !automatic && !recording;
        StopButton.IsEnabled = _coordinator.IsRecording && !busy;
        SaveClipButton.IsEnabled = recording && !busy;
        AddMomentButton.IsEnabled = recording && !busy;
        ClipDurationComboBox.IsEnabled = !busy;
        AudioButton.IsEnabled = _recorderReady && !busy;
        var qualityControlsEnabled = !busy && !automatic && !recording;
        ResolutionComboBox.IsEnabled = qualityControlsEnabled;
        FrameRateComboBox.IsEnabled = qualityControlsEnabled;
        AudioQualityComboBox.IsEnabled = qualityControlsEnabled;
        ApplyQualityButton.IsEnabled = qualityControlsEnabled;

        UpdateSidebarState(automatic, recording, target);

        SyncExperienceToggles();

        _trayIconService.Update(new TrayIconState(
            recording,
            automatic,
            _recorderReady && !busy,
            target?.Title) { CanToggleAutomaticCapture = AutoCaptureButton.IsEnabled });
    }

    private void UpdateSidebarState(bool automatic, bool recording, GameCaptureTarget? target)
    {
        var sidebarState = _isRecoveryBusy || _isSetupBusy
            ? ("Checking recorder", "WarningBrush")
            : _recordingFailure is not null
                ? ("Recording failed", "RecordingBrush")
                : recording
                    ? ("Recording", "RecordingBrush")
                    : automatic && _setupProcess is not null
                        ? ("Watching setup", "AccentBrush")
                    : automatic
                        ? ("Watching games", "AccentBrush")
                        : _recorderReady
                            ? ("Ready", "SuccessBrush")
                            : ("Recorder needs attention", "WarningBrush");
        SidebarStatusText.Text = sidebarState.Item1;
        SidebarStatusText.ToolTip = target?.Title;
        SidebarStatusDot.Fill = GetStatusBrush(sidebarState.Item2);

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
                    if (!AutoCaptureButton.IsEnabled) break;
                    await ExecuteCommandAsync(
                        "Toggle automatic capture",
                        () => SetAutomaticCaptureAsync(
                            !IsAutomaticRecordingEnabled,
                            persistPreference: true));
                    break;
                case TrayCommand.OpenRecordings:
                    ShowFromTray();
                    await SelectNavigationAsync(
                        RecordingsNavButton,
                        OpenRecordingLibraryPageAsync);
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
        if (WindowState == WindowState.Minimized) _recordingLibraryPage?.PauseReview();
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
        _audioMeterTimer.Stop();
        IsEnabled = false;
        if (_diagnosticsPage is not null) await _diagnosticsPage.StopPlaybackTestAsync();
        await _startupRecoveryCancellation.CancelAsync();
        _hotkeyService.Dispose();
        _trayIconService.CommandRequested -= TrayIconService_CommandRequested;
        _coordinator.RecordingStateChanged -= RecordingCoordinator_RecordingStateChanged;
        _automaticCaptureService.StatusChanged -=
            AutomaticCaptureService_StatusChanged;
        _recordingGuardian.SnapshotChanged -= RecordingGuardian_SnapshotChanged;
        _microphoneDeviceMonitor.StatusChanged -= MicrophoneDeviceMonitor_StatusChanged;
        if (_obsController is INativeRecorderController native)
        {
            native.RecordingFailed -= NativeRecorder_RecordingFailed;
        }

        try
        {
            await _recordingGuardian.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stop Recording Guardian while Blackbox was closing.");
        }

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

        await CloseRecordingSetupAsync(restoreAutomatic: false);
        if (_obsController is INativeRecorderController recorder)
        {
            try { await recorder.ShutdownAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not shut down the native recorder cleanly."); }
        }
        _trayIconService.Dispose();
        _recordingLibraryPage?.Dispose();
        _gameProfilesPage?.Dispose();
        _microphonePage?.Dispose();
        _startupRecoveryCancellation.Dispose();
        _shutdownComplete = true;
        Close();
    }

    private enum InlinePageSection
    {
        Help,
        Settings
    }

    private sealed record QualityOption<T>(string Label, T Value);

    private sealed record WorkspaceCommand(string Name, string Detail, string Glyph, Func<Task> Execute, Func<bool>? Available = null);

}
