using System.Windows;
using System.Windows.Threading;
using Blackbox.Infrastructure;

namespace Blackbox.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _audioMeterTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(250) };

    private void InitializeCaptureTelemetry()
    {
        CapturePage.IsVisibleChanged += (_, _) => UpdateAudioMeterTimer();
        IsVisibleChanged += (_, _) => UpdateAudioMeterTimer();
        _audioMeterTimer.Tick += (_, _) =>
        {
            if (_obsController is not INativeRecorderController { AudioSignal: { } audio }) return;
            UpdateAudioSignal(audio);
        };
    }

    private void UpdateAudioSignal(NativeAudioSignal audio)
    {
        GuardianAudioMeter.Value = GetAudioMeterValue(audio.PeakDb);
        GuardianAudioMeter.IsEnabled = audio.PeakDb is not null;
        GuardianAudioMeterRow.Visibility = audio.Active && audio.MixEnabled ? Visibility.Visible : Visibility.Collapsed;
        var mix = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) ? "Application mix" : "System mix";
        AudioSignalText.Text = !audio.MixEnabled ? $"{mix} off"
            : audio.PeakDb is { } peak ? $"{mix}  |  {(peak <= -60 ? "Quiet" : $"{peak:0.0} dBFS")}"
            : $"{mix}: waiting for audio";
        MicrophoneSignalText.Text = !audio.MicrophoneEnabled ? "Microphone off"
            : audio.MicrophoneReceiving ? "Microphone receiving audio" : "Microphone waiting for audio";
        MicrophoneSignalText.Visibility = audio.Active ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAudioMeterTimer()
    {
        if (!_isShutdownInProgress && IsVisible && CapturePage.IsVisible && _coordinator.IsRecording &&
            _obsController is INativeRecorderController) _audioMeterTimer.Start();
        else _audioMeterTimer.Stop();
    }
}
