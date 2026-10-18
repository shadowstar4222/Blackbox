using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

public partial class GameProfilesPage : UserControl, IDisposable
{
    private readonly IRunningApplicationCatalog _runningApplications;
    private readonly IGameProfileRepository _gameProfiles;
    private readonly IGameCaptureSelectionStore _captureSelectionStore;
    private readonly AutomaticCaptureService _automaticCaptureService;
    private readonly IGpuActivityProbe _gpuActivityProbe;
    private readonly GameProfileSettingsService _profileSettings;
    private readonly IClock _clock;
    private readonly ILogger<GameProfilesPage> _logger;
    private readonly bool _isNative;
    private readonly ObservableCollection<RunningApplicationListItem> _runningItems = [];
    private readonly ObservableCollection<GameProfileListItem> _profileItems = [];
    private readonly ObservableCollection<string> _aliasItems = [];
    private bool _isBusy;
    private bool _isSubscribed;

    public GameProfilesPage(
        IRunningApplicationCatalog runningApplications,
        IGameProfileRepository gameProfiles,
        IGameCaptureSelectionStore captureSelectionStore,
        AutomaticCaptureService automaticCaptureService,
        IGpuActivityProbe gpuActivityProbe,
        GameProfileSettingsService profileSettings,
        IClock clock,
        ILogger<GameProfilesPage> logger,
        IObsController recorder)
    {
        _runningApplications = runningApplications;
        _gameProfiles = gameProfiles;
        _captureSelectionStore = captureSelectionStore;
        _automaticCaptureService = automaticCaptureService;
        _gpuActivityProbe = gpuActivityProbe;
        _profileSettings = profileSettings;
        _clock = clock;
        _logger = logger;
        _isNative = recorder is INativeRecorderController;
        InitializeComponent();
        if (_isNative)
        {
            CaptureVoiceChatOverrideCheckBox.IsEnabled = false;
            CaptureVoiceChatOverrideCheckBox.ToolTip = "Native audio does not yet isolate voice chat from the system or game mix.";
            CropOverrideComboBox.IsEnabled = false;
            CropOverrideComboBox.ToolTip = "Native window cropping is not supported yet.";
            AutomaticMarkersOverrideComboBox.IsEnabled = false;
            AutomaticMarkersOverrideComboBox.ToolTip = "Automatic highlight analysis is not available yet. Manual markers remain available.";
            NativeCapabilityText.Visibility = Visibility.Visible;
        }
        RunningApplicationsList.ItemsSource = _runningItems;
        RememberedGamesList.ItemsSource = _profileItems;
        AliasesList.ItemsSource = _aliasItems;
        RunningApplicationsList.SelectionChanged += (_, _) => UpdateSelectionState();
        RememberedGamesList.SelectionChanged += (_, _) => LoadSelectedProfile();
        AliasesList.SelectionChanged += (_, _) => UpdateSelectionState();
        InitializeProfileOptions();
    }

    public async Task ActivateAsync()
    {
        if (!_isSubscribed)
        {
            _automaticCaptureService.StatusChanged += AutomaticCaptureService_StatusChanged;
            _isSubscribed = true;
        }

        await RefreshAsync();
    }

    public void Dispose()
    {
        if (!_isSubscribed)
        {
            return;
        }

        _automaticCaptureService.StatusChanged -= AutomaticCaptureService_StatusChanged;
        _isSubscribed = false;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void RememberButton_Click(object sender, RoutedEventArgs e)
    {
        if (RunningApplicationsList.SelectedItem is not RunningApplicationListItem selected)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            var now = _clock.UtcNow;
            var profiles = await _gameProfiles.GetAllAsync();
            var claimed = profiles.FirstOrDefault(profile =>
                profile.MatchesExecutablePath(selected.Application.ExecutablePath));
            if (claimed is not null && claimed.Identity != selected.Application.Identity)
            {
                SelectProfile(claimed.Identity);
                StatusText.Text = $"{selected.Application.ExecutableName} is already an alias for {claimed.DisplayName}.";
                return;
            }

            var profile = claimed is null
                ? new GameProfile(
                    selected.Application.ExecutablePath,
                    selected.Application.Title,
                    AutomaticRecordingEnabled: true,
                    now,
                    now)
                : claimed with
                {
                    DisplayName = selected.Application.Title,
                    AutomaticRecordingEnabled = true,
                    UpdatedAt = now
                };
            await _gameProfiles.UpsertAsync(profile);
            await LoadProfilesAsync(profile.Identity);
            StatusText.Text = $"Remembered {profile.DisplayName}.";
        });
    }

    private async void AddAliasButton_Click(object sender, RoutedEventArgs e)
    {
        if (RunningApplicationsList.SelectedItem is not RunningApplicationListItem selected ||
            RememberedGamesList.SelectedItem is not GameProfileListItem profileItem)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            var profiles = await _gameProfiles.GetAllAsync();
            var claimed = profiles.FirstOrDefault(profile =>
                profile.MatchesExecutablePath(selected.Application.ExecutablePath));
            if (claimed is not null)
            {
                StatusText.Text = claimed.Identity == profileItem.Profile.Identity
                    ? $"{selected.Application.ExecutableName} is already part of {claimed.DisplayName}."
                    : $"{selected.Application.ExecutableName} already belongs to {claimed.DisplayName}.";
                return;
            }

            var updated = profileItem.Profile with
            {
                ExecutableAliases = profileItem.Profile.ExecutableAliases
                    .Append(Path.GetFullPath(selected.Application.ExecutablePath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                UpdatedAt = _clock.UtcNow
            };
            await _gameProfiles.UpsertAsync(updated);
            profileItem.Profile = updated;
            LoadSelectedProfile();
            RememberedGamesList.Items.Refresh();
            UpdateRunningProfileStates();
            StatusText.Text = $"Added {selected.Application.ExecutableName} as an alias for {updated.DisplayName}.";
        });
    }

    private async void UseForCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (RunningApplicationsList.SelectedItem is not RunningApplicationListItem
            {
                Profile: not null
            } selected)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            var profile = selected.Profile;
            var selection = new GameCaptureSelection(
                profile.ExecutablePath,
                selected.Application.ExecutablePath,
                profile.DisplayName);
            _captureSelectionStore.Save(selection);

            var target = selected.Application
                .ToCaptureTarget(GameDetectionSource.ConfiguredExecutable) with
            {
                Title = profile.DisplayName,
                CaptureGameAudio = profile.CaptureGameAudio
            };
            await _automaticCaptureService.SelectTargetAsync(target);
            UpdateRunningProfileStates();
            StatusText.Text = _automaticCaptureService.Status.Message;
        });
    }

    private async void ProfilePreferenceCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (RememberedGamesList.SelectedItem is not GameProfileListItem item)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            var updated = item.Profile with
            {
                AutomaticRecordingEnabled = AutomaticRecordingCheckBox.IsChecked == true,
                CaptureGameAudio = CaptureGameAudioCheckBox.IsChecked == true,
                FollowLauncherHandoff = FollowLauncherHandoffCheckBox.IsChecked == true,
                PreferGpuActivity = PreferGpuActivityCheckBox.IsChecked == true,
                UpdatedAt = _clock.UtcNow
            };
            await _gameProfiles.UpsertAsync(updated);
            item.Profile = updated;
            RememberedGamesList.Items.Refresh();
            LoadSelectedProfile();
            UpdateRunningProfileStates();
            StatusText.Text = $"Updated capture settings for {updated.DisplayName}.";
        });
    }

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (RememberedGamesList.SelectedItem is not GameProfileListItem item)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            var updated = item.Profile with
            {
                Overrides = new GameProfileOverrides
                {
                    Resolution = ReadValueOverride<RecordingResolution>(ResolutionOverrideComboBox),
                    FramesPerSecond = ReadValueOverride<int>(FrameRateOverrideComboBox),
                    AudioBitrateKbps = ReadValueOverride<int>(AudioBitrateOverrideComboBox),
                    EncoderPreference =
                        ReadValueOverride<RecordingEncoderPreference>(EncoderOverrideComboBox),
                    QualityPreset =
                        ReadValueOverride<RecordingQualityPreset>(QualityPresetOverrideComboBox),
                    SegmentDurationMinutes =
                        ReadValueOverride<int>(SegmentDurationOverrideComboBox),
                    MaximumStorageGigabytes =
                        ReadValueOverride<decimal>(StorageLimitOverrideComboBox),
                    MaximumRetentionHours = ReadValueOverride<int>(RetentionOverrideComboBox),
                    CaptureDelaySeconds = ReadValueOverride<int>(CaptureDelayOverrideComboBox),
                    CaptureMicrophone = ReadValueOverride<bool>(CaptureMicrophoneOverrideComboBox),
                    CaptureVoiceChat = CaptureVoiceChatOverrideCheckBox.IsChecked,
                    MicrophoneProcessing =
                        ReadReferenceOverride<MicrophoneProcessingSettings>(
                            MicrophoneProcessingOverrideComboBox),
                    Crop = ReadReferenceOverride<CaptureCrop>(CropOverrideComboBox),
                    ExportPreset = ReadReferenceOverride<GameExportPreset>(ExportOverrideComboBox),
                    Notifications =
                        ReadReferenceOverride<GameNotificationPreferences>(
                            NotificationsOverrideComboBox),
                    InputControls = item.Profile.Overrides.InputControls,
                    AutomaticMarkers =
                        ReadReferenceOverride<AutomaticMarkerSettings>(
                            AutomaticMarkersOverrideComboBox)
                },
                UpdatedAt = _clock.UtcNow
            };
            updated.Validate();
            await _gameProfiles.UpsertAsync(updated);
            item.Profile = updated;
            RememberedGamesList.Items.Refresh();
            LoadSelectedProfile();
            StatusText.Text = $"Applied the recording profile for {updated.DisplayName}.";
        });
    }

    private async void ResetProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (RememberedGamesList.SelectedItem is not GameProfileListItem item)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            var updated = item.Profile with
            {
                Overrides = new GameProfileOverrides(),
                UpdatedAt = _clock.UtcNow
            };
            await _gameProfiles.UpsertAsync(updated);
            item.Profile = updated;
            RememberedGamesList.Items.Refresh();
            LoadSelectedProfile();
            StatusText.Text = $"{updated.DisplayName} now inherits every global setting.";
        });
    }

    private async void RemoveAliasButton_Click(object sender, RoutedEventArgs e)
    {
        if (RememberedGamesList.SelectedItem is not GameProfileListItem item ||
            AliasesList.SelectedItem is not string selectedAlias)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            var updated = item.Profile with
            {
                ExecutableAliases = item.Profile.ExecutableAliases
                    .Where(alias => !Path.GetFullPath(alias).Equals(
                        Path.GetFullPath(selectedAlias),
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                UpdatedAt = _clock.UtcNow
            };
            await _gameProfiles.UpsertAsync(updated);
            item.Profile = updated;
            LoadSelectedProfile();
            RememberedGamesList.Items.Refresh();
            UpdateRunningProfileStates();
            StatusText.Text = $"Removed {Path.GetFileName(selectedAlias)} from {updated.DisplayName}.";
        });
    }

    private async void RemoveGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GameProfileListItem item })
        {
            return;
        }

        var confirmation = MessageBox.Show(
            Window.GetWindow(this),
            $"Stop remembering {item.DisplayName}?",
            "Remove game",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            if (_captureSelectionStore.Current?.ProfileIdentity == item.Profile.Identity)
            {
                _captureSelectionStore.Clear();
            }

            await _gameProfiles.DeleteAsync(item.ExecutablePath);
            _profileItems.Remove(item);
            LoadSelectedProfile();
            UpdateRunningProfileStates();
            StatusText.Text = $"Removed {item.DisplayName}.";
        });
    }

    private async Task RefreshAsync()
    {
        await ExecuteAsync(async () =>
        {
            var applications = await _runningApplications.GetRunningApplicationsAsync();
            var gpuSnapshot = await _gpuActivityProbe.SampleAsync(
                applications
                    .Select(static application => application.ProcessId)
                    .Distinct()
                    .ToArray());
            _runningItems.Clear();
            foreach (var application in applications)
            {
                _runningItems.Add(new RunningApplicationListItem(
                    application,
                    gpuSnapshot.IsAvailable ? gpuSnapshot.GetUtilization(application.ProcessId) : null,
                    null,
                    false,
                    false));
            }

            await LoadProfilesAsync();
            StatusText.Text = applications.Count == 0
                ? "No taskbar application windows are currently open."
                : $"Found {applications.Count} open taskbar window(s).";
        });
    }

    private async Task LoadProfilesAsync(string? selectedIdentity = null)
    {
        selectedIdentity ??= (RememberedGamesList.SelectedItem as GameProfileListItem)?.Profile.Identity;
        var profiles = await _gameProfiles.GetAllAsync();
        _profileItems.Clear();
        foreach (var profile in profiles)
        {
            _profileItems.Add(new GameProfileListItem(profile));
        }

        SelectProfile(selectedIdentity);
        LoadSelectedProfile();
        UpdateRunningProfileStates();
    }

    private void SelectProfile(string? identity)
    {
        RememberedGamesList.SelectedItem = identity is null
            ? null
            : _profileItems.FirstOrDefault(item => item.Profile.Identity == identity);
    }

    private void LoadSelectedProfile()
    {
        _aliasItems.Clear();
        if (RememberedGamesList.SelectedItem is not GameProfileListItem item)
        {
            SelectedProfilePanel.IsEnabled = false;
            SelectedGameText.Text = "Selected game settings";
            EffectiveProfileText.Text = "Select a remembered game.";
            AutomaticRecordingCheckBox.IsChecked = false;
            CaptureGameAudioCheckBox.IsChecked = false;
            FollowLauncherHandoffCheckBox.IsChecked = false;
            PreferGpuActivityCheckBox.IsChecked = false;
            UpdateSelectionState();
            return;
        }

        SelectedProfilePanel.IsEnabled = !_isBusy;
        SelectedGameText.Text = $"{item.DisplayName} settings";
        AutomaticRecordingCheckBox.IsChecked = item.Profile.AutomaticRecordingEnabled;
        CaptureGameAudioCheckBox.IsChecked = item.Profile.CaptureGameAudio;
        FollowLauncherHandoffCheckBox.IsChecked = item.Profile.FollowLauncherHandoff;
        PreferGpuActivityCheckBox.IsChecked = item.Profile.PreferGpuActivity;
        LoadProfileOverrides(item.Profile);
        foreach (var alias in item.Profile.ExecutableAliases)
        {
            _aliasItems.Add(alias);
        }

        UpdateSelectionState();
    }

    private void InitializeProfileOptions()
    {
        ResolutionOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("Match application", RecordingResolution.MatchApplication),
            OverrideChoice.Create("1280 x 720", RecordingResolution.Hd720),
            OverrideChoice.Create("1920 x 1080", RecordingResolution.FullHd1080),
            OverrideChoice.Create("2560 x 1440", RecordingResolution.QuadHd1440),
            OverrideChoice.Create("3840 x 2160", RecordingResolution.UltraHd2160)
        };
        FrameRateOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("30 FPS", 30),
            OverrideChoice.Create("60 FPS", 60),
            OverrideChoice.Create("120 FPS", 120)
        };
        AudioBitrateOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("160 kbps", 160),
            OverrideChoice.Create("192 kbps", 192)
        };
        EncoderOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("Automatic", RecordingEncoderPreference.Automatic),
            OverrideChoice.Create("Hardware preferred", RecordingEncoderPreference.Hardware),
            OverrideChoice.Create("Software", RecordingEncoderPreference.Software)
        };
        QualityPresetOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("Performance", RecordingQualityPreset.Performance),
            OverrideChoice.Create("Balanced", RecordingQualityPreset.Balanced),
            OverrideChoice.Create("Quality", RecordingQualityPreset.Quality),
            OverrideChoice.Create("Maximum", RecordingQualityPreset.Maximum)
        };
        SegmentDurationOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("1 minute", 1),
            OverrideChoice.Create("2 minutes", 2),
            OverrideChoice.Create("5 minutes", 5),
            OverrideChoice.Create("10 minutes", 10)
        };
        StorageLimitOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("25 GB", 25m),
            OverrideChoice.Create("50 GB", 50m),
            OverrideChoice.Create("100 GB", 100m),
            OverrideChoice.Create("250 GB", 250m),
            OverrideChoice.Create("500 GB", 500m)
        };
        RetentionOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("6 hours", 6),
            OverrideChoice.Create("12 hours", 12),
            OverrideChoice.Create("24 hours", 24),
            OverrideChoice.Create("3 days", 72),
            OverrideChoice.Create("7 days", 168),
            OverrideChoice.Create("30 days", 720)
        };
        CaptureDelayOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("No delay", 0),
            OverrideChoice.Create("1 second", 1),
            OverrideChoice.Create("2 seconds", 2),
            OverrideChoice.Create("5 seconds", 5),
            OverrideChoice.Create("10 seconds", 10)
        };
        CaptureMicrophoneOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit("Inherit global microphone setting"),
            OverrideChoice.Create("Record microphone", true),
            OverrideChoice.Create("Do not record microphone", false)
        };
        CropOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("1% on every edge", UniformCrop(1)),
            OverrideChoice.Create("2% on every edge", UniformCrop(2)),
            OverrideChoice.Create("5% on every edge", UniformCrop(5))
        };
        ExportOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("MKV, original video", new GameExportPreset()),
            OverrideChoice.Create(
                "MP4, original video",
                new GameExportPreset { Container = GameExportContainer.Mp4 }),
            OverrideChoice.Create(
                "MP4, re-encode",
                new GameExportPreset
                {
                    Container = GameExportContainer.Mp4,
                    ReencodeVideo = true
                })
        };
        NotificationsOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("All events", new GameNotificationPreferences()),
            OverrideChoice.Create(
                "Failures and devices",
                new GameNotificationPreferences { RoutineEvents = false }),
            OverrideChoice.Create(
                "Failures only",
                new GameNotificationPreferences
                {
                    RoutineEvents = false,
                    DeviceChanges = false
                })
        };
        AutomaticMarkersOverrideComboBox.ItemsSource = new OverrideChoice[]
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("Off", new AutomaticMarkerSettings()),
            OverrideChoice.Create(
                "Audio spikes",
                new AutomaticMarkerSettings
                {
                    Enabled = true,
                    DetectAudioSpikes = true
                }),
            OverrideChoice.Create(
                "Major scene changes",
                new AutomaticMarkerSettings
                {
                    Enabled = true,
                    DetectSceneChanges = true
                }),
            OverrideChoice.Create(
                "Audio + scene changes",
                new AutomaticMarkerSettings
                {
                    Enabled = true,
                    DetectAudioSpikes = true,
                    DetectSceneChanges = true
                }),
            OverrideChoice.Create(
                "Sensitive highlights",
                new AutomaticMarkerSettings
                {
                    Enabled = true,
                    DetectAudioSpikes = true,
                    DetectSceneChanges = true,
                    AudioSpikeThresholdDb = -12,
                    SceneLuminanceDeltaThreshold = 0.2,
                    Cooldown = TimeSpan.FromSeconds(20)
                })
        };
    }

    private void LoadProfileOverrides(GameProfile profile)
    {
        var overrides = profile.Overrides;
        SelectOverride(ResolutionOverrideComboBox, overrides.Resolution);
        SelectOverride(FrameRateOverrideComboBox, overrides.FramesPerSecond);
        SelectOverride(AudioBitrateOverrideComboBox, overrides.AudioBitrateKbps is { } bitrate ? Math.Min(bitrate, 192) : null);
        SelectOverride(EncoderOverrideComboBox, overrides.EncoderPreference);
        SelectOverride(QualityPresetOverrideComboBox, overrides.QualityPreset);
        SelectOverride(SegmentDurationOverrideComboBox, overrides.SegmentDurationMinutes);
        SelectOverride(StorageLimitOverrideComboBox, overrides.MaximumStorageGigabytes);
        SelectOverride(RetentionOverrideComboBox, overrides.MaximumRetentionHours);
        SelectOverride(CaptureDelayOverrideComboBox, overrides.CaptureDelaySeconds);
        SelectOverride(CaptureMicrophoneOverrideComboBox, overrides.CaptureMicrophone);
        CaptureVoiceChatOverrideCheckBox.IsChecked = overrides.CaptureVoiceChat;
        LoadMicrophoneOptions(overrides.MicrophoneProcessing);
        SelectOverride(CropOverrideComboBox, overrides.Crop);
        SelectOverride(ExportOverrideComboBox, overrides.ExportPreset);
        SelectOverride(NotificationsOverrideComboBox, overrides.Notifications);
        SelectOverride(AutomaticMarkersOverrideComboBox, overrides.AutomaticMarkers);

        var effective = _profileSettings.Resolve(profile);
        EffectiveProfileText.Text =
            $"{FormatResolution(effective.RecordingQuality.Resolution)} | " +
            $"{effective.RecordingQuality.FramesPerSecond} FPS | " +
            $"{effective.RecordingQuality.ForNativeRecording().AudioBitrateKbps} kbps | " +
            $"{effective.Recording.SegmentDurationMinutes} min segments | " +
            $"mic {(effective.CaptureMicrophone ? "on" : "off")} | " +
            $"{(_isNative ? "auto analysis unavailable" : effective.AutomaticMarkers.Enabled ? "auto markers on" : "auto markers off")} | " +
            $"{effective.OverriddenFields.Count} override(s)";
    }

    private void LoadMicrophoneOptions(MicrophoneProcessingSettings? selected)
    {
        var global = _profileSettings.GlobalDefaults.MicrophoneProcessing;
        var options = new List<OverrideChoice>
        {
            OverrideChoice.Inherit(),
            OverrideChoice.Create("Current global processing", global),
            OverrideChoice.Create(
                "Natural voice",
                MicrophoneProcessingSettings.ForPreset(
                    MicrophoneVoicePreset.Natural,
                    global.InputGainDb)),
            OverrideChoice.Create(
                "Broadcast voice",
                MicrophoneProcessingSettings.ForPreset(
                    MicrophoneVoicePreset.Broadcast,
                    global.InputGainDb)),
            OverrideChoice.Create(
                "Strong noise removal",
                MicrophoneProcessingSettings.ForPreset(
                    MicrophoneVoicePreset.StrongNoiseRemoval,
                    global.InputGainDb))
        };
        if (selected is not null && options.All(option => !Equals(option.Value, selected)))
        {
            options.Add(OverrideChoice.Create("Saved custom processing", selected));
        }

        MicrophoneProcessingOverrideComboBox.ItemsSource = options;
        SelectOverride(MicrophoneProcessingOverrideComboBox, selected);
    }

    private static void SelectOverride(ComboBox comboBox, object? value)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<OverrideChoice>()
            .FirstOrDefault(choice => value is null
                ? !choice.HasOverride
                : choice.HasOverride && Equals(choice.Value, value));
        comboBox.SelectedIndex = comboBox.SelectedIndex < 0 ? 0 : comboBox.SelectedIndex;
    }

    private static T? ReadValueOverride<T>(ComboBox comboBox)
        where T : struct =>
        comboBox.SelectedItem is OverrideChoice { HasOverride: true, Value: T value }
            ? value
            : null;

    private static T? ReadReferenceOverride<T>(ComboBox comboBox)
        where T : class =>
        comboBox.SelectedItem is OverrideChoice { HasOverride: true, Value: T value }
            ? value
            : null;

    private static CaptureCrop UniformCrop(int percent) => new()
    {
        LeftPercent = percent,
        TopPercent = percent,
        RightPercent = percent,
        BottomPercent = percent
    };

    private static string FormatResolution(RecordingResolution resolution) => resolution switch
    {
        RecordingResolution.MatchApplication => "Match application",
        RecordingResolution.Hd720 => "1280 x 720",
        RecordingResolution.FullHd1080 => "1920 x 1080",
        RecordingResolution.QuadHd1440 => "2560 x 1440",
        RecordingResolution.UltraHd2160 => "3840 x 2160",
        _ => resolution.ToString()
    };

    private void UpdateSelectionState()
    {
        var runningItem = RunningApplicationsList.SelectedItem as RunningApplicationListItem;
        var runningSelected = runningItem is not null;
        var profileSelected = RememberedGamesList.SelectedItem is GameProfileListItem;
        RememberButton.IsEnabled = !_isBusy && runningSelected;
        AddAliasButton.IsEnabled = !_isBusy && runningSelected && profileSelected;
        UseForCaptureButton.IsEnabled =
            !_isBusy &&
            runningItem?.Profile is not null &&
            (!_automaticCaptureService.IsEnabled || runningItem.Profile.AutomaticRecordingEnabled);
        UseForCaptureButton.ToolTip = runningItem?.Profile is null
            ? "Remember this application before selecting it for capture"
            : _automaticCaptureService.IsEnabled && !runningItem.Profile.AutomaticRecordingEnabled
                ? "Enable Record automatically for this game, or turn automatic capture off, before selecting it"
                : "Switch video and game audio to this remembered application";
        RemoveAliasButton.IsEnabled = !_isBusy && profileSelected && AliasesList.SelectedItem is string;
        SelectedProfilePanel.IsEnabled = !_isBusy && profileSelected;
    }

    private void AutomaticCaptureService_StatusChanged(AutomaticCaptureStatus status)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            UpdateRunningProfileStates();
            UpdateSelectionState();
        });
    }

    private void UpdateRunningProfileStates()
    {
        var profiles = _profileItems.Select(static item => item.Profile).ToArray();
        var preferred = _captureSelectionStore.Current;
        var active = _automaticCaptureService.Status.Target;
        for (var index = 0; index < _runningItems.Count; index++)
        {
            var current = _runningItems[index];
            var profile = profiles.FirstOrDefault(candidate =>
                candidate.MatchesExecutablePath(current.Application.ExecutablePath));
            var isPreferred = preferred is not null &&
                profile?.Identity == preferred.ProfileIdentity &&
                current.Application.Identity == preferred.TargetIdentity;
            var isActive = active is not null &&
                active.ProcessId == current.Application.ProcessId &&
                active.ObsWindowIdentifier.Equals(
                    current.Application.ObsWindowIdentifier,
                    StringComparison.OrdinalIgnoreCase);
            var updated = current with
            {
                Profile = profile,
                IsPreferredForCapture = isPreferred,
                IsActiveCapture = isActive
            };
            if (updated != current)
            {
                _runningItems[index] = updated;
            }
        }

        ActiveCaptureText.Text = active is not null
            ? $"Active capture: {active.Title}"
            : preferred is not null
                ? $"Preferred capture: {preferred.DisplayName} (not currently active)"
                : "Active capture: Automatic selection";
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        RefreshButton.IsEnabled = false;
        UpdateSelectionState();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Game profile operation failed.");
            StatusText.Text = $"Could not update games: {ex.Message}";
        }
        finally
        {
            _isBusy = false;
            RefreshButton.IsEnabled = true;
            UpdateSelectionState();
        }
    }
}

internal sealed record RunningApplicationListItem(
    RunningApplication Application,
    double? GpuUtilization,
    GameProfile? Profile,
    bool IsPreferredForCapture,
    bool IsActiveCapture)
{
    public string Title => Application.Title;
    public string ExecutableName => Application.ExecutableName;
    public string ActivitySummary => GpuUtilization is null
        ? $"{Application.WindowWidth} x {Application.WindowHeight} | GPU unavailable"
        : $"{Application.WindowWidth} x {Application.WindowHeight} | GPU {GpuUtilization:0.0}%";
    public string CaptureSummary => IsActiveCapture
        ? "Active capture"
        : IsPreferredForCapture
            ? "Preferred capture"
            : Profile is null
                ? string.Empty
                : Profile.AutomaticRecordingEnabled
                    ? "Remembered"
                    : "Remembered | automatic recording paused";
}

internal sealed class GameProfileListItem(GameProfile profile)
{
    public GameProfile Profile { get; set; } = profile;
    public string DisplayName => Profile.DisplayName;
    public string ExecutablePath => Profile.ExecutablePath;
    public string PreferenceSummary =>
        $"{(Profile.AutomaticRecordingEnabled ? "Auto" : "Paused")} | " +
        $"Audio {(Profile.CaptureGameAudio ? "on" : "off")} | " +
        $"{Profile.ExecutableAliases.Count} alias(es) | " +
        $"{Profile.Overrides switch
        {
            { HasOverrides: true } => "Custom profile",
            _ => "Global profile"
        }}";
}

internal sealed record OverrideChoice(string Label, object? Value, bool HasOverride)
{
    public static OverrideChoice Inherit(string label = "Inherit global") => new(label, null, false);
    public static OverrideChoice Create(string label, object value) => new(label, value, true);
    public override string ToString() => Label;
}
