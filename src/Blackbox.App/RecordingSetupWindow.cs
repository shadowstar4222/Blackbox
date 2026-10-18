using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Blackbox.App;

internal sealed partial class RecordingSetupWindow : Window
{
    private readonly RecordingSetupConfiguration _configuration;
    private readonly RecordingSetupPattern _pattern;
    private readonly MediaElement? _backgroundVideo;
    private readonly Stopwatch _elapsed = new();
    private int _videoLoops;
    private readonly DispatcherTimer _monitor = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TextBlock _settings = Text("Waiting for Blackbox...", 14);
    private readonly TextBlock _message = Text("", 13);
    private readonly TextBlock _source = Text("", 13);
    private readonly Button _record = new() { Content = "Record", MinWidth = 92, IsEnabled = false };
    private readonly Button _stop = new() { Content = "Stop", MinWidth = 72, IsEnabled = false };
    private readonly Button _pause = new() { Content = "\uE769", FontFamily = new FontFamily("Segoe MDL2 Assets"), MinWidth = 36, ToolTip = "Pause scene and music" };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Value = 30, Width = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _warning;
    private RecordingSetupAudio? _audio;
    private string? _lastStatus;
    private bool _started;
    private bool _paused;
    private bool _visualReady;
    private string? _sceneError;
    private RecordingSetupSceneState? _publishedScene;
    private readonly TextBlock _recordingHint = Text("Music starts at 30% volume. Recording begins when you press Record.", 15);
    private readonly Button _startScene = new() { Content = "Start scene & music", Height = 38, Margin = new Thickness(0, 20, 0, 0) };

    internal RecordingSetupWindow(RecordingSetupConfiguration configuration)
    {
        _configuration = configuration;
        Title = "Blackbox Recording Setup";
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Blackbox.ico"));
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this));
        UseLayoutRounding = true;
        _record.Style = (Style)FindResource("PrimaryButtonStyle");
        _stop.Style = (Style)FindResource("DangerButtonStyle");
        _pause.Style = (Style)FindResource("IconButtonStyle");
        _startScene.Style = (Style)FindResource("PrimaryButtonStyle");
        ShowInTaskbar = true;
        Background = Brushes.Black;
        Foreground = Brushes.White;
        MinWidth = 800;
        MinHeight = 600;
        Width = Math.Min(configuration.SceneFps == 60 ? 1920 : 1280, SystemParameters.WorkArea.Width - 60);
        Height = Math.Min(configuration.SceneFps == 60 ? 1080 : 760, SystemParameters.WorkArea.Height - 60);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _pattern = new RecordingSetupPattern();
        var root = new Grid();
        root.Children.Add(_pattern);
        if (configuration.Moving)
        {
            _backgroundVideo = new MediaElement
            {
                LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
                Stretch = Stretch.Fill, IsMuted = true, Volume = 0, ScrubbingEnabled = true
            };
            _backgroundVideo.MediaEnded += (_, _) =>
            {
                _videoLoops++;
                _backgroundVideo.Position = TimeSpan.Zero;
                if (!_paused) _backgroundVideo.Play();
            };
            _backgroundVideo.MediaOpened += (_, _) => _visualReady = _backgroundVideo.HasVideo;
            _backgroundVideo.MediaFailed += (_, e) =>
            {
                _visualReady = false;
                _sceneError = "Background video unavailable: " + e.ErrorException.Message;
                _message.Text = _sceneError;
                SetPaused(true);
            };
            root.Children.Add(_backgroundVideo);
        }
        var overlay = new Grid { Margin = new Thickness(20) };
        overlay.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        overlay.RowDefinitions.Add(new RowDefinition());
        overlay.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(overlay);
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(Text("BLACKBOX / RECORDING SETUP", 20, true));
        _source.Margin = new Thickness(18, 4, 0, 0);
        heading.Children.Add(_source);
        overlay.Children.Add(Panel(heading));
        var details = new StackPanel { Margin = new Thickness(14) };
        details.Children.Add(Text("RECORDING SETTINGS", 14, true));
        _settings.Margin = new Thickness(0, 16, 0, 16);
        details.Children.Add(_settings);
        _message.Foreground = (Brush)FindResource("WarningBrush");
        details.Children.Add(_message);
        var settingsPanel = Panel(details);
        settingsPanel.Width = 292;
        settingsPanel.HorizontalAlignment = HorizontalAlignment.Right;
        settingsPanel.VerticalAlignment = VerticalAlignment.Stretch;
        settingsPanel.Margin = new Thickness(0, 8, 0, 8);
        settingsPanel.Child = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(settingsPanel, 1);
        overlay.Children.Add(settingsPanel);
        var controls = new WrapPanel { Margin = new Thickness(8) };
        controls.Children.Add(_record); controls.Children.Add(_stop); controls.Children.Add(_pause);
        var mute = new CheckBox { Content = "Mute music", Margin = new Thickness(12, 7, 8, 0) };
        controls.Children.Add(mute); controls.Children.Add(_volume);
        var warning = Text("Photosensitivity: bright patterns may trigger seizures. Stop if uncomfortable.", 12);
        warning.Width = 300;
        warning.Foreground = (Brush)FindResource("WarningBrush");
        controls.Children.Add(warning);
        foreach (var button in new[] { _record, _stop, _pause }) { button.Height = 32; button.Margin = new Thickness(0, 0, 8, 0); }
        var footer = Panel(controls);
        Grid.SetRow(footer, 2);
        overlay.Children.Add(footer);
        _record.Click += (_, _) => Send("record");
        _stop.Click += (_, _) => Send("stop");
        _pause.Click += (_, _) => SetPaused(!_paused);
        _volume.ValueChanged += (_, _) => _audio?.SetVolume(mute.IsChecked == true ? 0 : _volume.Value / 100);
        mute.Checked += (_, _) => _audio?.SetVolume(0);
        mute.Unchecked += (_, _) => _audio?.SetVolume(_volume.Value / 100);
        var consent = new StackPanel { MaxWidth = 560, Margin = new Thickness(28) };
        consent.Children.Add(Text(RecordingSetupProtocol.Warning, 19, true));
        consent.Children.Add(_recordingHint);
        consent.Children.Add(_startScene);
        _warning = new Border { Background = (Brush)FindResource("SidebarBrush"), Child = consent };
        root.Children.Add(_warning);
        Content = root;
        _startScene.Click += (_, _) => StartScene(playAudio: true);
        Loaded += (_, _) => { _monitor.Tick += Monitor; _monitor.Start(); Monitor(null, EventArgs.Empty); };
        Closed += (_, _) => { _monitor.Stop(); _backgroundVideo?.Close(); _audio?.Dispose(); };
    }

    private void StartScene(bool playAudio)
    {
        if (_started) return;
        _started = true;
        _warning.Visibility = Visibility.Collapsed;
        _elapsed.Start();
        _visualReady = _backgroundVideo is null;
        if (_backgroundVideo is not null)
        {
            var filename = _configuration.SceneFps == 60 ? "moving-1080p60.mp4" : "moving-720p30.mp4";
            _backgroundVideo.Source = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "RecordingSetup", filename));
            _backgroundVideo.Play();
        }
        if (!playAudio) return;
        try { _audio = new RecordingSetupAudio(_configuration.MusicPath); _audio.Play(); }
        catch (Exception ex) { _sceneError = "Music output unavailable: " + ex.Message; _message.Text = _sceneError; }
    }

    private void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused) { _elapsed.Stop(); _backgroundVideo?.Pause(); }
        else { _elapsed.Start(); _backgroundVideo?.Play(); }
        _pause.Content = paused ? "\uE768" : "\uE769";
        _pause.ToolTip = paused ? "Resume scene and music" : "Pause scene and music";
        try { if (paused) _audio?.Pause(); else _audio?.Play(); }
        catch (Exception ex) { _sceneError = "Music output unavailable: " + ex.Message; }
    }

    private void Send(string action)
    {
        _record.IsEnabled = _stop.IsEnabled = false;
        try { RecordingSetupProtocol.Write(Path.Combine(_configuration.Directory, "command.json"), new RecordingSetupCommand(Guid.NewGuid().ToString("N"), action)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _sceneError = "Could not send recording command: " + ex.Message; _message.Text = _sceneError; }
    }

    private void Monitor(object? sender, EventArgs e)
    {
        try
        {
            using var parent = Process.GetProcessById(_configuration.ParentId);
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != _configuration.ParentStartTicks) { Close(); return; }
        }
        catch (ArgumentException) { Close(); return; }
        var scene = new RecordingSetupSceneState(Environment.ProcessId, _started, _visualReady);
        if (scene != _publishedScene)
        {
            try { RecordingSetupProtocol.Write(Path.Combine(_configuration.Directory, "scene-state.json"), scene); _publishedScene = scene; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _message.Text = "Could not report setup readiness: " + ex.Message; }
        }
        var status = RecordingSetupProtocol.Read<RecordingSetupStatus>(Path.Combine(_configuration.Directory, "status.json"));
        if (status is null) return;
        _startScene.Content = status.AutoRecord ? "Start scene & record" : "Start scene & music";
        _recordingHint.Text = status.AutoRecord
            ? "Music starts at 30% volume. Automatic recording starts with the scene, using your current audio and microphone settings."
            : "Music starts at 30% volume. Recording begins when you press Record.";
        _record.IsEnabled = _started && _visualReady && status.CanRecord;
        _stop.IsEnabled = status.CanStop;
        var text = $"{status.State}\n\n{status.Video}\n\n{status.Audio}\n\n{status.Microphone}\n{status.Processing}";
        if (text != _lastStatus) { _settings.Text = text; _lastStatus = text; }
        _message.Text = _sceneError ?? status.Message;
        var dpi = VisualTreeHelper.GetDpi(this);
        var surface = (FrameworkElement)Content;
        var pattern = _configuration.Moving ? $"{_configuration.SceneFps} fps" : "Still bars";
        _source.Text = $"{surface.ActualWidth * dpi.DpiScaleX:0} x {surface.ActualHeight * dpi.DpiScaleY:0} scene | {pattern} | {_elapsed.Elapsed:hh\\:mm\\:ss}";
    }

    internal void StartForValidation(bool playAudio) => StartScene(playAudio);
    internal void PauseForValidation() => SetPaused(true);
    internal bool WarningVisible => _warning.Visibility == Visibility.Visible;
    internal string SettingsText => _settings.Text;

    private static TextBlock Text(string value, double size, bool bold = false) => new()
    {
        Text = value, FontSize = size, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(8)
    };

    private static Border Panel(UIElement child) => new()
    { Child = child, Background = new SolidColorBrush(Color.FromArgb(235, 22, 26, 30)), CornerRadius = new CornerRadius(3) };
}

internal sealed class RecordingSetupPattern : FrameworkElement
{
    private static readonly Brush[] Bars = [Brushes.Red, Brushes.Lime, Brushes.Yellow, Brushes.Blue, Brushes.Magenta, Brushes.Cyan];
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth; var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        for (var i = 0; i < Bars.Length; i++) dc.DrawRectangle(Bars[i], null, new Rect(i * width / 6, 0, width / 6 + 1, height));
    }
}
