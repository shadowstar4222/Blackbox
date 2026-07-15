using System.Windows;
using Blackbox.Domain;
using Blackbox.Recording;

namespace Blackbox.App;

public partial class MainWindow : Window
{
    private readonly RecordingCoordinator _coordinator;
    private readonly RecordingSettings _settings;
    private bool _isRecording;

    public MainWindow(RecordingCoordinator coordinator, RecordingSettings settings)
    {
        _coordinator = coordinator;
        _settings = settings;
        InitializeComponent();
        SegmentLengthText.Text = $"{_settings.SegmentDurationMinutes} minutes";
        LocationText.Text = _settings.RecordingLocation;
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

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isRecording)
        {
            await _coordinator.StopAsync();
        }

        base.OnClosing(e);
    }
}
