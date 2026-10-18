using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The page exposes deterministic disposal to its owning main window.")]
public partial class MicrophonePage : UserControl, IDisposable
{
    private static readonly TimeSpan MeasurementDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ComparisonDuration = TimeSpan.FromSeconds(5);
    private readonly MicrophoneCalibrationService _calibrationService;
    private readonly MicrophoneSelectionService _microphoneSelectionService;
    private readonly IMicrophoneConfigurationStore _configurationStore;
    private readonly ILogger<MicrophonePage> _logger;
    private readonly bool _isNative;
    private readonly CancellationTokenSource _pageCancellation = new();
    private MicrophoneCalibrationMeasurement? _backgroundNoise;
    private MicrophoneCalibrationMeasurement? _normalVoice;
    private MicrophoneCalibrationMeasurement? _loudVoice;
    private MicrophoneCalibrationResult? _result;
    private string? _beforePath;
    private string? _afterPath;
    private bool _isBusy;
    private bool _loadingProcessingControls;
    private MicrophoneProcessingSettings _editingSettings = new();
    private bool _disposed;

    public MicrophonePage(
        MicrophoneCalibrationService calibrationService,
        MicrophoneSelectionService microphoneSelectionService,
        IMicrophoneConfigurationStore configurationStore,
        ILogger<MicrophonePage> logger,
        IObsController recorder)
    {
        _calibrationService = calibrationService;
        _microphoneSelectionService = microphoneSelectionService;
        _configurationStore = configurationStore;
        _logger = logger;
        _isNative = recorder is INativeRecorderController;
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ComparisonPlayer.Close(); };
        VoicePresetComboBox.ItemsSource = new[]
        {
            new VoicePresetOption(MicrophoneVoicePreset.Natural, "Natural"),
            new VoicePresetOption(MicrophoneVoicePreset.Broadcast, "Broadcast"),
            new VoicePresetOption(MicrophoneVoicePreset.StrongNoiseRemoval, "Strong noise removal"),
            new VoicePresetOption(MicrophoneVoicePreset.Custom, "Custom")
        };
        if (_isNative)
        {
            NativeMicrophonePanel.Visibility = Visibility.Visible;
            ResetCalibrationText();
        }
    }

    public async Task ActivateAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ExecuteAsync("Load microphones", async () =>
        {
            var devices = await _calibrationService.GetDevicesAsync(_pageCancellation.Token);
            if (devices.Count == 0)
            {
                throw new InvalidOperationException("No active microphone devices were found in Windows.");
            }

            if (_configurationStore.Current.AutomaticallySelectDevice)
            {
                await _microphoneSelectionService.ResolveAsync(_pageCancellation.Token);
            }

            var configuration = _configurationStore.Current;
            LoadProcessingSettings(configuration.ProcessingSettings);
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
            StatusText.Text = string.Empty;
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
            var applied = await _microphoneSelectionService.ApplyAsync(_pageCancellation.Token);
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
        ResetCalibrationText();
        OpenBeforeButton.Visibility = Visibility.Collapsed;
        OpenAfterButton.Visibility = Visibility.Collapsed;
        if (IsLoaded)
        {
            StatusText.Text = _isNative ? "Microphone selected. Apply routing to save it." : "Ready to measure background noise.";
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

    private async void TestMicrophoneButton_Click(object sender, RoutedEventArgs e)
    {
        var measurement = await CaptureAsync(
            MicrophoneCalibrationPhase.NormalVoice,
            "Speak normally while Blackbox listens for three seconds.",
            NativeTestResultText);
        if (measurement is null)
        {
            return;
        }

        StatusText.Text = measurement.PeakDb > -55
            ? $"Microphone audio detected at {measurement.PeakDb:F1} dBFS."
            : $"The microphone opened, but its signal was very quiet ({measurement.PeakDb:F1} dBFS). Check the selected device.";
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
            await _calibrationService.SelectDeviceAsync(device, _pageCancellation.Token);
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
                _pageCancellation.Token);
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
                _pageCancellation.Token);
            LoadProcessingSettings(_configurationStore.Current.ProcessingSettings);
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
                _pageCancellation.Token);
            _beforePath = comparison.BeforePath;
            _afterPath = comparison.AfterPath;
            OpenBeforeButton.Visibility = Visibility.Visible;
            OpenAfterButton.Visibility = Visibility.Visible;
            StopComparisonButton.Visibility = Visibility.Visible;
            StatusText.Text = "Comparison complete. Listen to each sample to hear the difference.";
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
        catch (OperationCanceledException) when (_pageCancellation.IsCancellationRequested)
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
        LevelProgressBar.Visibility = _isBusy ? Visibility.Visible : Visibility.Collapsed;
        var hasDevice = DeviceComboBox.SelectedItem is MicrophoneDevice;
        DeviceComboBox.IsEnabled = !_isBusy && DeviceComboBox.Items.Count > 0;
        AutomaticSelectionCheckBox.IsEnabled = !_isBusy;
        ExcludedDevicesList.IsEnabled = !_isBusy;
        SaveRoutingButton.IsEnabled = !_isBusy && hasDevice;
        InputGainSlider.IsEnabled = !_isBusy;
        BackgroundButton.IsEnabled = !_isBusy && hasDevice;
        NormalButton.IsEnabled = !_isBusy && hasDevice;
        LoudButton.IsEnabled = !_isBusy && hasDevice;
        TestMicrophoneButton.IsEnabled = _isNative && !_isBusy && hasDevice;
        NativeMicrophonePanel.IsEnabled = !_isBusy;
        ApplyButton.IsEnabled = !_isBusy && _result is not null;
        CompareButton.IsEnabled = !_isBusy && _result is not null;
    }

    private void ResetCalibrationText()
    {
        RecommendationText.Text = "Complete all three measurements";
        WarningText.Text = "Waiting for measurements";
    }

    private async void ApplyVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("Apply microphone processing", async () =>
        {
            var settings = ReadProcessingSettings();
            await SaveAndApplyProcessingAsync(settings);
            StatusText.Text = settings.ProcessingEnabled
                ? $"{FormatPreset(settings.Preset)} voice processing is active."
                : "Voice processing is bypassed; input gain remains active.";
        });
    }

    private void VoicePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProcessingControls || !IsLoaded ||
            VoicePresetComboBox.SelectedItem is not VoicePresetOption option)
        {
            return;
        }

        var settings = option.Value == MicrophoneVoicePreset.Custom
            ? _editingSettings with { Preset = MicrophoneVoicePreset.Custom }
            : MicrophoneProcessingSettings.ForPreset(option.Value, InputGainSlider.Value);
        LoadProcessingSettings(settings);
        StatusText.Text = $"{option.Name} settings loaded. Apply processing to save them.";
    }

    private void ProcessingControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingProcessingControls || !IsLoaded)
        {
            return;
        }

        _editingSettings = ReadProcessingSettings() with { Preset = MicrophoneVoicePreset.Custom };
        _loadingProcessingControls = true;
        VoicePresetComboBox.SelectedItem = VoicePresetComboBox.Items
            .OfType<VoicePresetOption>()
            .First(static option => option.Value == MicrophoneVoicePreset.Custom);
        _loadingProcessingControls = false;
    }

    private void LoadProcessingSettings(MicrophoneProcessingSettings settings)
    {
        _loadingProcessingControls = true;
        try
        {
            _editingSettings = settings;
            VoicePresetComboBox.SelectedItem = VoicePresetComboBox.Items
                .OfType<VoicePresetOption>()
                .First(option => option.Value == settings.Preset);
            ProcessingEnabledCheckBox.IsChecked = settings.ProcessingEnabled;
            InputGainSlider.Value = settings.InputGainDb;
            HighPassCheckBox.IsChecked = settings.HighPassEnabled;
            HighPassCutoffSlider.Value = settings.HighPassCutoffHz;
            NoiseSuppressionCheckBox.IsChecked = settings.NoiseSuppressionEnabled;
            NoiseSuppressionSlider.Value = settings.NoiseSuppressionDb;
            ExpanderCheckBox.IsChecked = settings.ExpanderEnabled;
            ExpanderThresholdSlider.Value = settings.ExpanderThresholdDb;
            LowMidEqSlider.Value = settings.LowMidEqDb;
            PresenceEqSlider.Value = settings.PresenceEqDb;
            CompressorCheckBox.IsChecked = settings.CompressorEnabled;
            CompressorThresholdSlider.Value = settings.CompressorThresholdDb;
            DeEsserCheckBox.IsChecked = settings.DeEsserEnabled;
            DeEsserThresholdSlider.Value = settings.DeEsserThresholdDb;
            LimiterCheckBox.IsChecked = settings.LimiterEnabled;
            LimiterThresholdSlider.Value = settings.LimiterThresholdDb;
        }
        finally
        {
            _loadingProcessingControls = false;
        }
    }

    private MicrophoneProcessingSettings ReadProcessingSettings()
    {
        var preset = VoicePresetComboBox.SelectedItem is VoicePresetOption option
            ? option.Value
            : MicrophoneVoicePreset.Custom;
        return _editingSettings with
        {
            Preset = preset,
            ProcessingEnabled = ProcessingEnabledCheckBox.IsChecked == true,
            InputGainDb = InputGainSlider.Value,
            HighPassEnabled = HighPassCheckBox.IsChecked == true,
            HighPassCutoffHz = HighPassCutoffSlider.Value,
            NoiseSuppressionEnabled = NoiseSuppressionCheckBox.IsChecked == true,
            NoiseSuppressionDb = NoiseSuppressionSlider.Value,
            ExpanderEnabled = ExpanderCheckBox.IsChecked == true,
            ExpanderThresholdDb = ExpanderThresholdSlider.Value,
            LowMidEqDb = LowMidEqSlider.Value,
            PresenceEqDb = PresenceEqSlider.Value,
            CompressorEnabled = CompressorCheckBox.IsChecked == true,
            CompressorThresholdDb = CompressorThresholdSlider.Value,
            DeEsserEnabled = DeEsserCheckBox.IsChecked == true,
            DeEsserThresholdDb = DeEsserThresholdSlider.Value,
            LimiterEnabled = LimiterCheckBox.IsChecked == true,
            LimiterThresholdDb = LimiterThresholdSlider.Value
        };
    }

    private async Task SaveAndApplyProcessingAsync(MicrophoneProcessingSettings settings)
    {
        settings.Validate();
        _configurationStore.Save(_configurationStore.Current with { ProcessingSettings = settings });
        await _microphoneSelectionService.ApplyAsync(settings, _pageCancellation.Token);
        _editingSettings = settings;
    }

    private static string FormatPreset(MicrophoneVoicePreset preset) => preset switch
    {
        MicrophoneVoicePreset.StrongNoiseRemoval => "Strong noise removal",
        _ => preset.ToString()
    };

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

            ComparisonPlayer.Close();
            ComparisonPlayer.Source = new Uri(recordingPath, UriKind.Absolute);
            ComparisonPlayer.Play();
            StatusText.Text = $"Playing {Path.GetFileName(recordingPath)} inside Blackbox.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open microphone comparison failed.");
            StatusText.Text = $"Open comparison failed: {ex.Message}";
        }
    }

    private void StopComparisonButton_Click(object sender, RoutedEventArgs e) => ComparisonPlayer.Stop();
    private void ComparisonPlayer_MediaEnded(object sender, RoutedEventArgs e) => ComparisonPlayer.Stop();
    private void ComparisonPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ComparisonPlayer.Close();
        StatusText.Text = $"Comparison playback failed: {e.ErrorException.Message}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ComparisonPlayer.Close();
        _pageCancellation.Cancel();
        _pageCancellation.Dispose();
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

    private sealed record VoicePresetOption(MicrophoneVoicePreset Value, string Name)
    {
        public override string ToString() => Name;
    }
}
