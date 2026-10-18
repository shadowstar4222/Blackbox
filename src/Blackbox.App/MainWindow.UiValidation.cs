using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Blackbox.Recording;

namespace Blackbox.App;

public partial class MainWindow
{
    internal async Task ValidateWorkspaceAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        Show();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (_isRecoveryBusy) await Task.Delay(50, timeout.Token);
        await ThemeUiValidation.ValidateAsync(this, outputDirectory);
        if (Blackbox.Export.InstantClipDurationParser.Parse(ClipDurationComboBox.Text) != TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("The clip-duration control did not preserve its initial value.");

        foreach (var size in new[] { new Size(1240, 780), new Size(1024, 680) })
        {
            Width = size.Width;
            Height = size.Height;
            HomeNavButton.IsChecked = true;
            SetCaptureHeader();
            await ValidateConsolidatedStatusAsync(outputDirectory, size.Width);
            await SaveWorkspaceImageAsync(outputDirectory, $"capture-{size.Width}");
            OpenCommandPalette();
            CommandSearchBox.Text = "microphone";
            if (CommandResultsList.Items.Count != 1) throw new InvalidOperationException("Command search did not return the microphone action.");
            await SaveWorkspaceImageAsync(outputDirectory, $"commands-{size.Width}");
            if (!CommandSearchBox.IsKeyboardFocusWithin)
                throw new InvalidOperationException("Quick actions did not focus its search field.");
            CloseCommandPalette();
            await SelectNavigationAsync(RecordingsNavButton, OpenRecordingLibraryPageAsync);
            await _recordingLibraryPage!.ValidateSessionFiltersAsync();
            if (size.Width == 1240)
            {
                await _recordingLibraryPage.ValidateCompactTimelineAsync(outputDirectory, name => SaveWorkspaceImageAsync(outputDirectory, name));
            }
            await SaveWorkspaceImageAsync(outputDirectory, $"recordings-{size.Width}");
            if (Environment.GetEnvironmentVariable("BLACKBOX_PLAYBACK_SMOKE") == "1")
                await _recordingLibraryPage.ValidateEmbeddedPlaybackAsync(outputDirectory,
                    () => SaveWorkspaceImageAsync(outputDirectory, $"review-{size.Width}"),
                    () =>
                    {
                        BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (CapturePage.IsVisible) throw new InvalidOperationException("Review back skipped the recording library.");
                    });
            await SelectNavigationAsync(GamesNavButton, OpenGameProfilesPageAsync);
            await _gameProfilesPage!.ValidatePixelScrollingAsync();
            await SaveWorkspaceImageAsync(outputDirectory, $"games-{size.Width}");
            await SelectNavigationAsync(MicrophoneNavButton, OpenMicrophonePageAsync);
            await SaveWorkspaceImageAsync(outputDirectory, $"microphone-{size.Width}");
            for (var tab = 0; tab < 3; tab++)
            {
                _microphonePage!.MicrophoneTabs.SelectedIndex = tab;
                if (tab == 0) _microphonePage.AdvancedVoiceExpander.IsExpanded = true;
                await SaveWorkspaceImageAsync(outputDirectory, $"microphone-tab-{tab}-{size.Width}");
            }
            _microphonePage!.MicrophoneTabs.SelectedIndex = 0;
            _microphonePage.AdvancedVoiceExpander.IsExpanded = false;
            await SelectNavigationAsync(DiagnosticsNavButton, OpenDiagnosticsPageAsync);
            await _diagnosticsPage!.ValidateScrollingAsync(outputDirectory, size.Width, name => SaveWorkspaceImageAsync(outputDirectory, name));
            await SaveWorkspaceImageAsync(outputDirectory, $"diagnostics-{size.Width}");
            _diagnosticsPage!.ValidateSetupConsent();
            await SaveWorkspaceImageAsync(outputDirectory, $"diagnostics-setup-{size.Width}");
            _diagnosticsPage.DiagnosticsTabs.SelectedIndex = 2;
            await SaveWorkspaceImageAsync(outputDirectory, $"diagnostics-details-{size.Width}");
            _diagnosticsPage.DiagnosticsTabs.SelectedIndex = 0;
            SettingsNavButton.IsChecked = true;
            await SaveWorkspaceImageAsync(outputDirectory, $"settings-{size.Width}");
        }

        if (Environment.GetEnvironmentVariable("BLACKBOX_PLAYBACK_IDLE_SMOKE") == "1")
        {
            await Task.Delay(10000);
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(5000);
            var percent = (process.TotalProcessorTime - cpu).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100;
            File.WriteAllText(Path.Combine(outputDirectory, "playback-settled-idle.txt"),
                $"Five-second settled idle after all review players closed: {percent:F2}% of one CPU core.");
        }

        File.WriteAllText(Path.Combine(outputDirectory, "result.txt"),
            "PASS: all pages and microphone/diagnostics tabs rendered at 1240x780 and 1024x680; one sidebar status; positive capture-health and audio feedback restored; warnings/errors preserved; setup consent revoked on tab change; command search, session filters, and virtualized pixel scrolling passed." +
            (Environment.GetEnvironmentVariable("BLACKBOX_PLAYBACK_SMOKE") == "1"
                ? " Embedded decoding, frame presentation, seek coalescing, timeline caching, and finalized-segment follow passed." : string.Empty));
    }

    private async Task ValidateConsolidatedStatusAsync(string directory, double width)
    {
        await _recordingGuardian.StopAsync();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            SetStatus(null);
            var idle = RecordingGuardianSnapshot.Stopped(_clock.UtcNow) with
            { State = RecordingGuardianState.Waiting, Message = "Watching for a selected window." };
            UpdateGuardianDetails(idle);
            if (GuardianMessageText.Visibility != Visibility.Collapsed || GuardianVideoText.Visibility != Visibility.Collapsed ||
                GuardianAudioMeterRow.Visibility != Visibility.Collapsed || StatusText.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Routine idle status or unavailable telemetry is still visible.");
            foreach (var state in new[] { (Auto: false, Recording: false, Text: "Ready"),
                         (Auto: true, Recording: false, Text: "Watching games"),
                         (Auto: true, Recording: true, Text: "Recording") })
            {
                UpdateSidebarState(state.Auto, state.Recording, null);
                UpdateLayout();
                if (VisibleTextBlocks(ShellRoot).Count(text => text.Text == state.Text) != 1 || SidebarStatusText.Text != state.Text)
                    throw new InvalidOperationException($"{state.Text} is missing or duplicated outside the sidebar.");
                if (VisibleTextBlocks(CaptureToolbar).Any(text => text.Text is "Ready" or "Watching games" or "Recording"))
                    throw new InvalidOperationException("The recording toolbar contains a duplicate text status.");
                await SaveWorkspaceImageAsync(directory, $"status-{state.Text.Replace(' ', '-')}-{width}");
            }
            foreach (var health in new[] { RecordingGuardianState.Warning, RecordingGuardianState.Repairing, RecordingGuardianState.Faulted })
            {
                UpdateGuardianDetails(idle with { State = health, Issue = RecordingGuardianIssue.BlackFrame,
                    Message = "Capture needs attention.", OutputWidth = 1280, OutputHeight = 720, FramesPerSecond = 60,
                    GameAudioPeakDb = -18 });
                if (GuardianMessageText.Visibility != Visibility.Visible || GuardianVideoText.Visibility != Visibility.Visible ||
                    GuardianAudioMeterRow.Visibility != Visibility.Visible)
                    throw new InvalidOperationException("Important capture-health or telemetry details were hidden.");
            }
            await SaveWorkspaceImageAsync(directory, $"health-warning-{width}");
            UpdateGuardianDetails(idle with { State = RecordingGuardianState.Healthy });
            if (GuardianMessageText.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("A resolved health warning stayed visible.");
            UpdateGuardianDetails(idle with { State = RecordingGuardianState.Healthy, RecordingOutputActive = true,
                OutputWidth = 1920, OutputHeight = 1080, FramesPerSecond = 60, GameAudioPeakDb = -12,
                MicrophoneCaptureEnabled = true, MicrophonePacketReceived = true });
            if (CaptureHealthSummaryText.Text != "Video and output verified" ||
                GuardianAudioMeter.Value <= 0 || !AudioSignalText.Text.Contains("dBFS", StringComparison.Ordinal) ||
                MicrophoneSignalText.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Healthy recording telemetry is empty.");
            await SaveWorkspaceImageAsync(directory, $"health-active-{width}");
            UpdateAudioSignal(new(true, true, -96, true, false));
            if (!AudioSignalText.Text.Contains("Quiet", StringComparison.Ordinal) || GuardianAudioMeter.Value != 0 ||
                MicrophoneSignalText.Text != "Microphone waiting for audio")
                throw new InvalidOperationException("Silent audio was confused with missing telemetry.");
            UpdateAudioSignal(new(true, true, null, false, false));
            if (!AudioSignalText.Text.Contains("waiting", StringComparison.Ordinal) || GuardianAudioMeter.IsEnabled)
                throw new InvalidOperationException("Unavailable audio did not report its waiting state.");
            UpdateAudioSignal(new(true, false, null, false, false));
            if (!AudioSignalText.Text.EndsWith("off", StringComparison.Ordinal) || GuardianAudioMeterRow.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Disabled audio did not report its off state.");
            if (_audioMeterTimer.IsEnabled) throw new InvalidOperationException("The audio UI timer ran while not recording.");
            SetStatus("Recording command failed: validation message.");
            if (StatusText.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Action failures were hidden by status consolidation.");
        }
        finally
        {
            SetStatus(null);
            UpdateGuardianDetails(_recordingGuardian.Snapshot);
            UpdateRecordingControls();
            await _recordingGuardian.StartAsync();
        }
    }

    private static IEnumerable<TextBlock> VisibleTextBlocks(DependencyObject root)
    {
        if (root is TextBlock { IsVisible: true } text) yield return text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisibleTextBlocks(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private async Task SaveWorkspaceImageAsync(string directory, string name)
    {
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ShellRoot.ActualWidth),
            (int)Math.Ceiling(ShellRoot.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(ShellRoot);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }
}
