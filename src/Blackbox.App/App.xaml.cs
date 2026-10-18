using System.IO;
using System.Windows;
using Blackbox.Capture;
using Blackbox.Domain;
using Blackbox.Export;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Blackbox.Storage;
using Blackbox.App.Hotkeys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace Blackbox.App;

public partial class App : Application
{
    private IHost? _host;
    private FileStream? _instanceLease;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args is ["--recording-setup-target", var setupConfiguration])
        {
            try
            {
                var configuration = RecordingSetupProtocol.Read<RecordingSetupConfiguration>(setupConfiguration)
                    ?? throw new InvalidDataException("Invalid recording setup configuration.");
                if (configuration.SceneFps is not (30 or 60) || !Path.IsPathFullyQualified(configuration.Directory))
                    throw new InvalidDataException("Invalid recording setup scene settings.");
                var window = new RecordingSetupWindow(configuration);
                MainWindow = window;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                window.Show();
                if (configuration.Validate) await window.ValidateSetupWindowAsync();
            }
            catch (Exception ex)
            {
                File.WriteAllText(setupConfiguration + ".error.txt", ex.ToString());
                Shutdown(-1);
            }
            return;
        }

        var customData = Environment.GetEnvironmentVariable("BLACKBOX_DATA_DIRECTORY");
        var appData = string.IsNullOrWhiteSpace(customData) ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Blackbox") : Path.GetFullPath(customData);

        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Information();
        if (Environment.GetEnvironmentVariable("BLACKBOX_WINDOW_CATALOG_DEBUG") == "1")
        {
            loggerConfiguration.MinimumLevel.Override(
                "Blackbox.Infrastructure.WindowsRunningApplicationCatalog",
                LogEventLevel.Debug);
        }

        Log.Logger = loggerConfiguration
            .WriteTo.File(Path.Combine(appData, "logs", "blackbox-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            Directory.CreateDirectory(appData);
            try { _instanceLease = new FileStream(Path.Combine(appData, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                MessageBox.Show("Blackbox is already running. Open it from the system tray.", "Blackbox", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }
            _host = Host.CreateDefaultBuilder(e.Args)
                .UseSerilog()
                .ConfigureServices(services =>
                {
                    var databasePath = Path.Combine(appData, "blackbox.db");
                    var recordingPath = string.IsNullOrWhiteSpace(customData) ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                        "Blackbox") : Path.Combine(appData, "Recordings");

                    services.AddSingleton(new RecordingSettings { RecordingLocation = recordingPath });
                    services.AddSingleton(new ObsProvisioningOptions
                    {
                        PortableRootDirectory = Path.Combine(appData, "obs-portable"),
                        ConnectionSettingsPath = Path.Combine(appData, "obs-connection.json"),
                        MicrophoneSettingsPath = Path.Combine(appData, "microphone.json"),
                        AutomaticCaptureSettingsPath = Path.Combine(appData, "automatic-capture.json"),
                        GameCaptureSelectionSettingsPath = Path.Combine(appData, "capture-selection.json")
                    });
                    services.AddSingleton(new FfmpegOptions
                    {
                        RootDirectory = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg"),
                        WorkDirectory = Path.Combine(appData, "ffmpeg-work"),
                        TimelineCacheDirectory = Path.Combine(appData, "timeline-cache")
                    });
                    services.AddSingleton(new InstantClipOptions
                    {
                        OutputDirectory = Path.Combine(
                            recordingPath,
                            RecordingDirectoryLayout.InstantClipName)
                    });
                    services.AddSingleton(new RecordingRecoveryOptions());
                    services.AddSingleton(new DiagnosticLogOptions
                    {
                        LogDirectory = Path.Combine(appData, "logs")
                    });
                    services.AddSingleton(new SupportBundleOptions());
                    services.AddSingleton(new UserExperienceOptions
                    {
                        SettingsPath = Path.Combine(appData, "experience.json")
                    });
                    services.AddSingleton<IClock, SystemClock>();
                    services.AddSingleton<IDiagnosticLogReader, DiagnosticLogReader>();
                    services.AddSingleton<SupportBundleService>();
                    services.AddSingleton<UserExperienceSettingsStore>();
                    services.AddSingleton<RecordingQualityContext>();
                    services.AddSingleton<IRecordingQualitySettingsProvider>(provider =>
                        provider.GetRequiredService<RecordingQualityContext>());
                    services.AddSingleton<IGameCaptureTransformSettingsProvider>(provider =>
                        provider.GetRequiredService<RecordingQualityContext>());
                    services.AddSingleton<IWindowsStartupManager, WindowsStartupManager>();
                    services.AddSingleton<ISegmentRepository>(_ => new SqliteSegmentRepository(databasePath));
                    services.AddSingleton<IClipRepository>(_ => new SqliteClipRepository(databasePath));
                    services.AddSingleton<IGameProfileRepository>(_ => new SqliteGameProfileRepository(databasePath));
                    services.AddSingleton<ISegmentUsageRegistry, SegmentUsageRegistry>();
                    services.AddSingleton<IFfmpegProvisioner, BundledFfmpegProvisioner>();
                    services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
                    services.AddSingleton<RecordingSessionCatalog>();
                    services.AddSingleton<RecordingLibraryService>();
                    services.AddSingleton<IFfmpegCommandRunner, FfmpegCommandRunner>();
                    services.AddSingleton<SessionExportService>();
                    services.AddSingleton<InstantClipService>();
                    services.AddSingleton<MarkerRetentionService>();
                    services.AddSingleton<RecordingNoteService>();
                    services.AddHostedService<InstantClipBackgroundService>();
                    services.AddSingleton<PlaybackRuntime>();
                    services.AddSingleton<TimelineAssetService>();
                    services.AddSingleton<RecordingRecoveryService>();
                    services.AddSingleton<MicrophoneConfigurationStore>();
                    services.AddSingleton<IMicrophoneConfigurationStore>(provider =>
                        provider.GetRequiredService<MicrophoneConfigurationStore>());
                    services.AddSingleton<AutomaticCapturePreferenceStore>();
                    services.AddSingleton<IAutomaticCapturePreferenceStore>(provider =>
                        provider.GetRequiredService<AutomaticCapturePreferenceStore>());
                    services.AddSingleton<GameCaptureSelectionStore>();
                    services.AddSingleton<IGameCaptureSelectionStore>(provider =>
                        provider.GetRequiredService<GameCaptureSelectionStore>());
                    services.AddSingleton(new NativeCaptureOptions());
                    services.AddSingleton<NativeWorkerClient>();
                    services.AddSingleton<NativeRecordingController>();
                    services.AddSingleton<IRecordingTimelineClock>(provider => provider.GetRequiredService<NativeRecordingController>());
                    services.AddSingleton<IObsController>(provider =>
                        provider.GetRequiredService<NativeRecordingController>());
                    services.AddSingleton<INativeRecorderController>(provider =>
                        provider.GetRequiredService<NativeRecordingController>());
                    services.AddSingleton<IObsCaptureHealthClient>(provider =>
                        provider.GetRequiredService<NativeRecordingController>());
                    services.AddSingleton(new GpuActivityOptions());
                    services.AddSingleton<IGpuActivityProbe, WindowsGpuActivityProbe>();
                    services.AddSingleton<IProcessExecutablePathResolver, SteamProcessExecutablePathResolver>();
                    services.AddSingleton<IRunningApplicationCatalog, WindowsRunningApplicationCatalog>();
                    services.AddSingleton<IGameProcessDetector, WindowsGameProcessDetector>();
                    services.AddSingleton<NativeMicrophoneController>();
                    services.AddSingleton<IObsMicrophoneController>(provider =>
                        provider.GetRequiredService<NativeMicrophoneController>());
                    services.AddSingleton<IObsAudioMeterClient>(provider =>
                        provider.GetRequiredService<NativeMicrophoneController>());
                    services.AddSingleton<ManualCaptureTargetResolver>();
                    services.AddSingleton<IDefaultMicrophoneProvider, WindowsDefaultMicrophoneProvider>();
                    services.AddSingleton<MicrophoneSelectionService>();
                    services.AddSingleton(new MicrophoneMonitoringOptions());
                    services.AddSingleton<MicrophoneDeviceMonitor>();
                    services.AddSingleton<IMicrophoneDeviceMonitor>(provider =>
                        provider.GetRequiredService<MicrophoneDeviceMonitor>());
                    services.AddSingleton<MicrophoneCalibrationAnalyzer>();
                    services.AddSingleton<MicrophoneCalibrationService>();
                    services.AddSingleton<GameProfileSettingsService>();
                    services.AddSingleton<RecordingCoordinator>();
                    services.AddSingleton(new AutomaticCaptureOptions());
                    services.AddSingleton<AutomaticCaptureController>();
                    services.AddSingleton<AutomaticCaptureService>();
                    services.AddSingleton(new RecordingGuardianOptions());
                    services.AddSingleton(new TimelineMarkerOptions());
                    services.AddSingleton<IRecordingFrameAnalyzer, WpfRecordingFrameAnalyzer>();
                    services.AddSingleton<IRecordingGuardianContext, RecordingGuardianContext>();
                    services.AddSingleton<IRecordingGuardianRepairer, RecordingGuardianRepairer>();
                    services.AddSingleton<RecordingGuardianService>();
                    services.AddSingleton<AudioConfigurationService>();
                    services.AddSingleton<StartupRecoveryState>();
                    services.AddSingleton<StartupRecoveryCoordinator>();
                    services.AddSingleton<DiagnosticsService>();
                    services.AddSingleton<ProtectionService>();
                    services.AddSingleton<LiveMarkerService>();
                    services.AddSingleton<StorageQuotaEnforcer>();
                    services.AddSingleton<GlobalHotkeyService>();
                    services.AddSingleton<TrayIconService>();
                    services.AddTransient<MicrophonePage>();
                    services.AddSingleton<Func<MicrophonePage>>(provider =>
                        () => provider.GetRequiredService<MicrophonePage>());
                    services.AddTransient<RecordingLibraryPage>();
                    services.AddSingleton<Func<RecordingLibraryPage>>(provider =>
                        () => provider.GetRequiredService<RecordingLibraryPage>());
                    services.AddTransient<GameProfilesPage>();
                    services.AddSingleton<Func<GameProfilesPage>>(provider =>
                        () => provider.GetRequiredService<GameProfilesPage>());
                    services.AddTransient<DiagnosticsPage>();
                    services.AddSingleton<Func<DiagnosticsPage>>(provider =>
                        () => provider.GetRequiredService<DiagnosticsPage>());
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync();
            await _host.Services.GetRequiredService<ISegmentRepository>().InitializeAsync();
            await _host.Services.GetRequiredService<IClipRepository>().InitializeAsync();
            await _host.Services.GetRequiredService<IGameProfileRepository>().InitializeAsync();
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            if (e.Args is ["--automatic-capture-smoke", var automaticCaptureOutput])
            {
                if (string.IsNullOrWhiteSpace(customData))
                    throw new InvalidOperationException("Automatic-capture validation requires an isolated BLACKBOX_DATA_DIRECTORY.");
                await mainWindow.ValidateAutomaticCaptureAsync(automaticCaptureOutput);
                mainWindow.PrepareForSystemShutdown();
                Shutdown(0);
                return;
            }
            if (e.Args is ["--recording-setup-smoke", var playbackTestOutput])
            {
                if (string.IsNullOrWhiteSpace(customData))
                    throw new InvalidOperationException("Playback-test validation requires an isolated BLACKBOX_DATA_DIRECTORY.");
                await mainWindow.ValidateRecordingSetupAsync(playbackTestOutput);
                mainWindow.PrepareForSystemShutdown();
                Shutdown(0);
                return;
            }
            if (e.Args is ["--playback-transition-smoke", var transitionConfiguration])
            {
                if (string.IsNullOrWhiteSpace(customData))
                    throw new InvalidOperationException("Transition validation requires an isolated BLACKBOX_DATA_DIRECTORY.");
                await mainWindow.ValidatePlaybackTransitionsAsync(transitionConfiguration);
                mainWindow.PrepareForSystemShutdown();
                Shutdown(0);
                return;
            }
            if (e.Args is ["--performance-benchmark", var benchmarkConfiguration])
            {
                if (string.IsNullOrWhiteSpace(customData))
                    throw new InvalidOperationException("Performance validation requires an isolated BLACKBOX_DATA_DIRECTORY.");
                await mainWindow.BenchmarkPlaybackAsync(benchmarkConfiguration);
                mainWindow.PrepareForSystemShutdown();
                Shutdown(0);
                return;
            }
            if (e.Args.Contains("--ui-smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(customData))
                    throw new InvalidOperationException("UI validation requires an isolated BLACKBOX_DATA_DIRECTORY.");
                await mainWindow.ValidateWorkspaceAsync(Path.Combine(appData, "ui-snapshots"));
                mainWindow.PrepareForSystemShutdown();
                Shutdown(0);
                return;
            }
            if (e.Args.Any(static argument =>
                    argument.Equals("--startup-smoke-test", StringComparison.OrdinalIgnoreCase)))
            {
                var gameProfilesPage = _host.Services.GetRequiredService<GameProfilesPage>();
                var recordingLibraryPage = _host.Services.GetRequiredService<RecordingLibraryPage>();
                var microphonePage = _host.Services.GetRequiredService<MicrophonePage>();
                _ = _host.Services.GetRequiredService<DiagnosticsPage>();
                var recorder = _host.Services.GetRequiredService<NativeRecordingController>();
                var microphone = _host.Services.GetRequiredService<NativeMicrophoneController>();
                if (!ReferenceEquals(recorder, _host.Services.GetRequiredService<IObsController>()) ||
                    !ReferenceEquals(recorder, _host.Services.GetRequiredService<INativeRecorderController>()) ||
                    !ReferenceEquals(recorder, _host.Services.GetRequiredService<IObsCaptureHealthClient>()) ||
                    !ReferenceEquals(microphone, _host.Services.GetRequiredService<IObsMicrophoneController>()) ||
                    !ReferenceEquals(microphone, _host.Services.GetRequiredService<IObsAudioMeterClient>()))
                {
                    throw new InvalidOperationException("Native recording services do not share their singleton instances.");
                }

                gameProfilesPage.Dispose();
                recordingLibraryPage.Dispose();
                microphonePage.Dispose();
                Log.Information(
                    "Main window, all workspace pages, and native singleton mappings passed the packaged startup construction smoke test. Capture runtime was not probed.");
                Shutdown(0);
                return;
            }

            var experienceSettings = _host.Services
                .GetRequiredService<UserExperienceSettingsStore>()
                .Current;
            try
            {
                if (string.IsNullOrWhiteSpace(customData))
                    _host.Services.GetRequiredService<IWindowsStartupManager>().SetEnabled(experienceSettings.StartWithWindows);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not refresh the Blackbox Windows startup registration.");
            }

            mainWindow.StartHidden = e.Args.Any(static argument =>
                argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Blackbox could not complete application startup.");
            if (e.Args.Contains("--playback-transition-smoke", StringComparer.OrdinalIgnoreCase) ||
                e.Args.Contains("--automatic-capture-smoke", StringComparer.OrdinalIgnoreCase) ||
                e.Args.Contains("--recording-setup-smoke", StringComparer.OrdinalIgnoreCase) ||
                e.Args.Contains("--performance-benchmark", StringComparer.OrdinalIgnoreCase) ||
                e.Args.Contains("--ui-smoke-test", StringComparer.OrdinalIgnoreCase) ||
                e.Args.Contains("--startup-smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                _host?.Services.GetService<MainWindow>()?.PrepareForSystemShutdown();
                Shutdown(-1);
                return;
            }
            MessageBox.Show(
                $"Blackbox could not start.\n\n{ex.GetBaseException().Message}\n\n" +
                $"Details were written to:\n{Path.Combine(appData, "logs")}",
                "Blackbox startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _host?.Services.GetService<MainWindow>()?.PrepareForSystemShutdown();
        base.OnSessionEnding(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            if (_host is IAsyncDisposable asyncHost)
            {
                await asyncHost.DisposeAsync();
            }
            else
            {
                _host.Dispose();
            }
        }

        _instanceLease?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
