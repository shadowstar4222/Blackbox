using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

public partial class GameProfilesWindow : Window
{
    private readonly IRunningApplicationCatalog _runningApplications;
    private readonly IGameProfileRepository _gameProfiles;
    private readonly IGpuActivityProbe _gpuActivityProbe;
    private readonly IClock _clock;
    private readonly ILogger<GameProfilesWindow> _logger;
    private readonly ObservableCollection<RunningApplicationListItem> _runningItems = [];
    private readonly ObservableCollection<GameProfileListItem> _profileItems = [];
    private readonly ObservableCollection<string> _aliasItems = [];
    private bool _isBusy;

    public GameProfilesWindow(
        IRunningApplicationCatalog runningApplications,
        IGameProfileRepository gameProfiles,
        IGpuActivityProbe gpuActivityProbe,
        IClock clock,
        ILogger<GameProfilesWindow> logger)
    {
        _runningApplications = runningApplications;
        _gameProfiles = gameProfiles;
        _gpuActivityProbe = gpuActivityProbe;
        _clock = clock;
        _logger = logger;
        InitializeComponent();
        RunningApplicationsList.ItemsSource = _runningItems;
        RememberedGamesList.ItemsSource = _profileItems;
        AliasesList.ItemsSource = _aliasItems;
        RunningApplicationsList.SelectionChanged += (_, _) => UpdateSelectionState();
        RememberedGamesList.SelectionChanged += (_, _) => LoadSelectedProfile();
        AliasesList.SelectionChanged += (_, _) => UpdateSelectionState();
        Loaded += async (_, _) => await RefreshAsync();
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
            StatusText.Text = $"Added {selected.Application.ExecutableName} as an alias for {updated.DisplayName}.";
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
            StatusText.Text = $"Updated capture settings for {updated.DisplayName}.";
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
            this,
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
            await _gameProfiles.DeleteAsync(item.ExecutablePath);
            _profileItems.Remove(item);
            LoadSelectedProfile();
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
                    gpuSnapshot.IsAvailable ? gpuSnapshot.GetUtilization(application.ProcessId) : null));
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
        foreach (var alias in item.Profile.ExecutableAliases)
        {
            _aliasItems.Add(alias);
        }

        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        var runningSelected = RunningApplicationsList.SelectedItem is RunningApplicationListItem;
        var profileSelected = RememberedGamesList.SelectedItem is GameProfileListItem;
        RememberButton.IsEnabled = !_isBusy && runningSelected;
        AddAliasButton.IsEnabled = !_isBusy && runningSelected && profileSelected;
        RemoveAliasButton.IsEnabled = !_isBusy && profileSelected && AliasesList.SelectedItem is string;
        SelectedProfilePanel.IsEnabled = !_isBusy && profileSelected;
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
    double? GpuUtilization)
{
    public string Title => Application.Title;
    public string ExecutableName => Application.ExecutableName;
    public string ActivitySummary => GpuUtilization is null
        ? $"{Application.WindowWidth} x {Application.WindowHeight} | GPU unavailable"
        : $"{Application.WindowWidth} x {Application.WindowHeight} | GPU {GpuUtilization:0.0}%";
}

internal sealed class GameProfileListItem(GameProfile profile)
{
    public GameProfile Profile { get; set; } = profile;
    public string DisplayName => Profile.DisplayName;
    public string ExecutablePath => Profile.ExecutablePath;
    public string PreferenceSummary =>
        $"{(Profile.AutomaticRecordingEnabled ? "Auto" : "Paused")} | " +
        $"Audio {(Profile.CaptureGameAudio ? "on" : "off")} | " +
        $"{Profile.ExecutableAliases.Count} alias(es)";
}
