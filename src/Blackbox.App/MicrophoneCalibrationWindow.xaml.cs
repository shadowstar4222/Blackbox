using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using Blackbox.Domain;
using Blackbox.Recording;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WPF window disposes its cancellation source from its Closed event.")]
public partial class MicrophoneCalibrationWindow : Window
{
    private static readonly TimeSpan MeasurementDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ComparisonDuration = TimeSpan.FromSeconds(5);
    private readonly MicrophoneCalibrationService _calibrationService;
    private readonly MicrophoneSelectionService _microphoneSelectionService;
    private readonly IMicrophoneConfigurationStore _configurationStore;
    private readonly ILogger<MicrophoneCalibrationWindow> _logger;
    private readonly CancellationTokenSource _windowCancellation = new();
    private MicrophoneCalibrationMeasurement? _backgroundNoise;
    private MicrophoneCalibrationMeasurement? _normalVoice;
    private MicrophoneCalibrationMeasurement? _loudVoice;
    private MicrophoneCalibrationResult? _result;
    private string? _beforePath;
    private string? _afterPath;
    private bool _isBusy;

    public MicrophoneCalibrationWindow(
        MicrophoneCalibrationService calibrationService,
        MicrophoneSelectionService microphoneSelectionService,
        IMicrophoneConfigurationStore configurationStore,
        ILogger<MicrophoneCalibrationWindow> logger)
    {
        _calibrationService = calibrationService;
        _microphoneSelectionService = microphoneSelectionService;
        _configurationStore = configurationStore;
        _logger = logger;
        InitializeComponent();
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Load microphones", async () =>
        {
            var devices = await _calibrationService.GetDevicesAsync(_windowCancellation.Token);
            if (devices.Count == 0)
            {
                throw new InvalidOperationException("OBS did not report any microphone devices.");
            }

            if (_configurationStore.Current.AutomaticallySelectDevice)
            {
                await _microphoneSelectionService.ResolveAsync(_windowCancellation.Token);
            }

            var configuration = _configurationStore.Current;
            DeviceComboBox.ItemsSource = devices;
            DeviceComboBox.SelectedItem = devices.FirstOrDefault(device =>
                device.Id.Equals(configuration.DeviceId, StringComparison.OrdinalIgnoreCase))
                ?? devices[0];
            AutomaticSelectionCheckBox.IsChecked = configuration.AutomaticallySelectDevice;
            ExcludedDevicesList.ItemsSource = devices
                .Where(static device => !device.Id.Equals("default", StringComparison.OrdinalIgnoreCase))
                .Select(device => new MicrophoneExclusionListItem(
                    device.Id,
                    device.Name,
                    configuration.ExcludedDeviceIds.Contains(
                        device.Id,
                        StringComparer.OrdinalIgnoreCase)))
                .ToArray();
            StatusText.Text = configuration.AutomaticallySelectDevice
                ? $"Automatic routing is ready to use {configuration.DeviceName}."
                : "Manual routing is ready. You can also measure background noise.";
        });
    }

    private async void SaveRoutingButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Apply microphone routing", async () =>
        {
            var selected = GetSelectedDevice();
            var automatic = AutomaticSelectionCheckBox.IsChecked == true;
            var exclusions = ExcludedDevicesList.Items
                .OfType<MicrophoneExclusionListItem>()
                .Where(static item => item.IsExcluded)
                .Select(static item => item.Id)
                .ToArray();
            var configuration = _configurationStore.Current with
            {
                AutomaticallySelectDevice = automatic,
                ExcludedDeviceIds = exclusions,
                DeviceId = automatic ? _configurationStore.Current.DeviceId : selected.Id,
                DeviceName = automatic ? _configurationStore.Current.DeviceName : selected.Name
            };
            _configurationStore.Save(configuration);
            var applied = await _microphoneSelectionService.ApplyAsync(_windowCancellation.Token);
            DeviceComboBox.SelectedItem = DeviceComboBox.Items
                .OfType<MicrophoneDevice>()
                .FirstOrDefault(device =>
                    device.Id.Equals(applied.Id, StringComparison.OrdinalIgnoreCase))
                ?? selected;
            StatusText.Text = automatic
                ? $"Following the Windows default microphone: {applied.Name}."
                : $"Using {applied.Name} until you change it.";
        });
    }

    private async void BackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        var measurement = await CaptureAsync(
            MicrophoneCalibrationPhase.BackgroundNoise,
            "Stay quiet while Blackbox measures the room.",
            BackgroundResultText);
        if (measurement is not null)
        {
            _backgroundNoise = measurement;
        }

        AnalyzeWhenReady();
    }

    private void DeviceComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _backgroundNoise = null;
        _normalVoice = null;
        _loudVoice = null;
        _result = null;
        _beforePath = null;
        _afterPath = null;
        BackgroundResultText.Text = "Not measured";
        NormalResultText.Text = "Not measured";
        LoudResultText.Text = "Not measured";
        RecommendationText.Text = "Complete all three measurements";
        WarningText.Text = "Waiting for measurements";
        OpenBeforeButton.Visibility = Visibility.Collapsed;
        OpenAfterButton.Visibility = Visibility.Collapsed;
        if (IsLoaded)
        {
            StatusText.Text = "Ready to measure background noise.";
        }

        UpdateControlState();
    }

    private async void NormalButton_Click(object sender, RoutedEventArgs e)
    {
        var measurement = await CaptureAsync(
            MicrophoneCalibrationPhase.NormalVoice,
            "Speak normally for three seconds.",
            NormalResultText);
        if (measurement is not null)
        {
            _normalVoice = measurement;
        }

        AnalyzeWhenReady();
    }

    private async void LoudButton_Click(object sender, RoutedEventArgs e)
    {
        var measurement = await CaptureAsync(
            MicrophoneCalibrationPhase.LoudVoice,
            "Speak at your loudest expected gaming volume.",
            LoudResultText);
        if (measurement is not null)
        {
            _loudVoice = measurement;
        }

        AnalyzeWhenReady();
    }

    private async Task<MicrophoneCalibrationMeasurement?> CaptureAsync(
        MicrophoneCalibrationPhase phase,
        string status,
        System.Windows.Controls.TextBlock resultText)
    {
        MicrophoneCalibrationMeasurement? measurement = null;
        await ExecuteAsync("Measure microphone", async () =>
        {
            var device = GetSelectedDevice();
            await _calibrationService.SelectDeviceAsync(device, _windowCancellation.Token);
            StatusText.Text = status;
            LevelProgressBar.Value = 0;
            var progress = new Progress<AudioLevelSnapshot>(snapshot =>
            {
                LevelProgressBar.Value = Math.Clamp((snapshot.PeakDb + 60) / 60 * 100, 0, 100);
                resultText.Text = $"Live peak {snapshot.PeakDb:F1} dBFS";
            });
            measurement = await _calibrationService.CaptureAsync(
                phase,
                MeasurementDuration,
                progress,
                _windowCancellation.Token);
            resultText.Text = FormatMeasurement(measurement);
            StatusText.Text = "Measurement complete.";
        });

        return measurement;
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Apply microphone calibration", async () =>
        {
            await _calibrationService.ApplyAsync(
                GetSelectedDevice(),
                GetCalibrationResult(),
                _windowCancellation.Token);
            StatusText.Text = "Microphone recommendations applied.";
        });
    }

    private async void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Record microphone comparison", async () =>
        {
            StatusText.Text = "Recording the unprocessed sample, then the processed sample...";
            var comparison = await _calibrationService.RecordComparisonAsync(
                GetSelectedDevice(),
                GetCalibrationResult(),
                ComparisonDuration,
                _windowCancellation.Token);
            _beforePath = comparison.BeforePath;
            _afterPath = comparison.AfterPath;
            OpenBeforeButton.Visibility = Visibility.Visible;
            OpenAfterButton.Visibility = Visibility.Visible;
            StatusText.Text = "Comparison complete. Open each sample to hear the difference.";
        });
    }

    private void OpenBeforeButton_Click(object sender, RoutedEventArgs e) => TryOpenFile(_beforePath);

    private void OpenAfterButton_Click(object sender, RoutedEventArgs e) => TryOpenFile(_afterPath);

    private void AnalyzeWhenReady()
    {
        if (_backgroundNoise is null || _normalVoice is null || _loudVoice is null)
        {
            return;
        }

        _result = _calibrationService.Analyze(_backgroundNoise, _normalVoice, _loudVoice);
        RecommendationText.Text =
            $"Gain {_result.RecommendedGainDb:+0.0;-0.0;0.0} dB, " +
            $"expander {_result.RecommendedExpanderThresholdDb:F1} dB, " +
            $"compressor {_result.RecommendedCompressorThresholdDb:F1} dB";

        var warnings = new List<string>();
        if (_result.ClippingDetected)
        {
            warnings.Add("Clipping detected. Lower the microphone level in Windows and measure again.");
        }

        if (_result.AutomaticGainControlSuspected)
        {
            warnings.Add("Automatic gain control may be active. Disable it in the microphone software if possible.");
        }

        WarningText.Text = warnings.Count == 0
            ? "Levels have usable headroom and no automatic gain behavior was detected."
            : string.Join(" ", warnings);
        StatusText.Text = "Calibration recommendations are ready.";
        UpdateControlState();
    }

    private async Task ExecuteAsync(string commandName, Func<Task> command)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        UpdateControlState();
        try
        {
            await command();
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{CommandName} failed.", commandName);
            StatusText.Text = $"{commandName} failed: {ex.Message}";
        }
        finally
        {
            _isBusy = false;
            UpdateControlState();
        }
    }

    private void UpdateControlState()
    {
        var hasDevice = DeviceComboBox.SelectedItem is MicrophoneDevice;
        DeviceComboBox.IsEnabled = !_isBusy && DeviceComboBox.Items.Count > 0;
        AutomaticSelectionCheckBox.IsEnabled = !_isBusy;
        ExcludedDevicesList.IsEnabled = !_isBusy;
        SaveRoutingButton.IsEnabled = !_isBusy && hasDevice;
        BackgroundButton.IsEnabled = !_isBusy && hasDevice;
        NormalButton.IsEnabled = !_isBusy && hasDevice;
        LoudButton.IsEnabled = !_isBusy && hasDevice;
        ApplyButton.IsEnabled = !_isBusy && _result is not null;
        CompareButton.IsEnabled = !_isBusy && _result is not null;
    }

    private MicrophoneDevice GetSelectedDevice() =>
        DeviceComboBox.SelectedItem as MicrophoneDevice
        ?? throw new InvalidOperationException("Select a microphone first.");

    private MicrophoneCalibrationResult GetCalibrationResult() =>
        _result ?? throw new InvalidOperationException("Complete all three microphone measurements first.");

    private static string FormatMeasurement(MicrophoneCalibrationMeasurement measurement) =>
        $"Peak {measurement.PeakDb:F1} dBFS | Average {measurement.RmsDb:F1} dBFS";

    private void TryOpenFile(string? path)
    {
        try
        {
            var recordingPath = path
                ?? throw new InvalidOperationException("The comparison recording could not be found.");
            if (!File.Exists(recordingPath))
            {
                throw new InvalidOperationException("The comparison recording could not be found.");
            }

            Process.Start(new ProcessStartInfo(recordingPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open microphone comparison failed.");
            StatusText.Text = $"Open comparison failed: {ex.Message}";
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _windowCancellation.Cancel();
        _windowCancellation.Dispose();
    }

    private sealed class MicrophoneExclusionListItem(
        string id,
        string name,
        bool isExcluded)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public bool IsExcluded { get; set; } = isExcluded;
    }
}
