using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

public partial class GameProfilesWindow : Window
{
    private readonly IRunningApplicationCatalog _runningApplications;
    private readonly IGameProfileRepository _gameProfiles;
    private readonly IClock _clock;
    private readonly ILogger<GameProfilesWindow> _logger;
    private readonly ObservableCollection<RunningApplicationListItem> _runningItems = [];
    private readonly ObservableCollection<GameProfileListItem> _profileItems = [];
    private bool _isBusy;

    public GameProfilesWindow(
        IRunningApplicationCatalog runningApplications,
        IGameProfileRepository gameProfiles,
        IClock clock,
        ILogger<GameProfilesWindow> logger)
    {
        _runningApplications = runningApplications;
        _gameProfiles = gameProfiles;
        _clock = clock;
        _logger = logger;
        InitializeComponent();
        RunningApplicationsList.ItemsSource = _runningItems;
        RememberedGamesList.ItemsSource = _profileItems;
        RunningApplicationsList.SelectionChanged += (_, _) =>
            RememberButton.IsEnabled = !_isBusy && RunningApplicationsList.SelectedItem is not null;
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
            var existing = (await _gameProfiles.GetAllAsync())
                .FirstOrDefault(profile => profile.Identity == selected.Application.Identity);
            var profile = new GameProfile(
                selected.Application.ExecutablePath,
                selected.Application.Title,
                AutomaticRecordingEnabled: true,
                existing?.AddedAt ?? now,
                now);
            await _gameProfiles.UpsertAsync(profile);
            await LoadProfilesAsync();
            StatusText.Text = $"Remembered {profile.DisplayName}.";
        });
    }

    private async void AutomaticRecordingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: GameProfileListItem item })
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            var updated = item.Profile with
            {
                AutomaticRecordingEnabled = item.AutomaticRecordingEnabled,
                UpdatedAt = _clock.UtcNow
            };
            await _gameProfiles.UpsertAsync(updated);
            item.Profile = updated;
            StatusText.Text = updated.AutomaticRecordingEnabled
                ? $"Automatic recording enabled for {updated.DisplayName}."
                : $"Automatic recording paused for {updated.DisplayName}.";
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
            StatusText.Text = $"Removed {item.DisplayName}.";
        });
    }

    private async Task RefreshAsync()
    {
        await ExecuteAsync(async () =>
        {
            var applications = await _runningApplications.GetRunningApplicationsAsync();
            _runningItems.Clear();
            foreach (var application in applications)
            {
                _runningItems.Add(new RunningApplicationListItem(application));
            }

            await LoadProfilesAsync();
            StatusText.Text = applications.Count == 0
                ? "No suitable application windows are currently running."
                : $"Found {applications.Count} running application(s).";
        });
    }

    private async Task LoadProfilesAsync()
    {
        var profiles = await _gameProfiles.GetAllAsync();
        _profileItems.Clear();
        foreach (var profile in profiles)
        {
            _profileItems.Add(new GameProfileListItem(profile));
        }
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        RefreshButton.IsEnabled = false;
        RememberButton.IsEnabled = false;
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
            RememberButton.IsEnabled = RunningApplicationsList.SelectedItem is not null;
        }
    }
}

internal sealed record RunningApplicationListItem(RunningApplication Application)
{
    public string Title => Application.Title;
    public string ExecutableName => Application.ExecutableName;
    public string WindowSize => $"{Application.WindowWidth} x {Application.WindowHeight}";
}

internal sealed class GameProfileListItem(GameProfile profile)
{
    public GameProfile Profile { get; set; } = profile;
    public string DisplayName => Profile.DisplayName;
    public string ExecutablePath => Profile.ExecutablePath;
    public bool AutomaticRecordingEnabled { get; set; } = profile.AutomaticRecordingEnabled;
}
