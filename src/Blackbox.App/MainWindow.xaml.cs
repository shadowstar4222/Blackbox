using System.Windows;
using System.Windows.Interop;
using Blackbox.App.Hotkeys;
using Blackbox.Domain;
using Blackbox.Recording;
using Blackbox.Storage;

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
    private bool _isRecording;

    public MainWindow(
        RecordingCoordinator coordinator,
        ObsAutoSetupService obsAutoSetupService,
        AudioConfigurationService audioConfigurationService,
        ProtectionService protectionService,
        StorageQuotaEnforcer storageQuotaEnforcer,
        GlobalHotkeyService hotkeyService,
        RecordingSettings settings)
    {
        _coordinator = coordinator;
        _obsAutoSetupService = obsAutoSetupService;
        _audioConfigurationService = audioConfigurationService;
        _protectionService = protectionService;
        _storageQuotaEnforcer = storageQuotaEnforcer;
        _hotkeyService = hotkeyService;
        _settings = settings;
        InitializeComponent();
        SegmentLengthText.Text = $"{_settings.SegmentDurationMinutes} minutes";
        LocationText.Text = _settings.RecordingLocation;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hotkeyService.Attach(new WindowInteropHelper(this));
        _hotkeyService.Register(
            new GlobalHotkey(1, HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.NoRepeat, 0x76),
            ProtectLastFiveMinutesAsync);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        await _coordinator.StartAsync(_settings);
        _isRecording = true;
        StatusText.Text = "Recording";
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await _coordinator.StopAsync();
        _isRecording = false;
        StatusText.Text = "Idle";
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
    }

    private async void ProtectButton_Click(object sender, RoutedEventArgs e)
    {
        await ProtectLastFiveMinutesAsync();
    }

    private async void PruneButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await _storageQuotaEnforcer.EnforceAsync(_settings);
        StatusText.Text = $"Pruned {result.DeletedSegments} segment(s)";
    }

    private async void AudioButton_Click(object sender, RoutedEventArgs e)
    {
        await _audioConfigurationService.ApplyAsync(AudioRoutingProfile.Default, new MicrophoneProcessingSettings());
        StatusText.Text = "Applied audio routing";
    }

    private async void ObsSetupButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await _obsAutoSetupService.SetupAsync(new ObsConnectionSettings(), _settings);
        StatusText.Text = result.Message;
    }

    private async Task ProtectLastFiveMinutesAsync()
    {
        await _protectionService.ProtectPreviousFiveMinutesAsync();
        StatusText.Text = "Protected previous 5 minutes";
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _hotkeyService.Dispose();

        if (_isRecording)
        {
            await _coordinator.StopAsync();
        }

        base.OnClosing(e);
    }
}
