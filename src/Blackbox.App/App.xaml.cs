using System.IO;
using System.Net.Http;
using System.Windows;
using Blackbox.Domain;
using Blackbox.Export;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Blackbox.Storage;
using Blackbox.App.Hotkeys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Blackbox.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Blackbox");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(appData, "logs", "blackbox-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        _host = Host.CreateDefaultBuilder(e.Args)
            .UseSerilog()
            .ConfigureServices(services =>
            {
                var databasePath = Path.Combine(appData, "blackbox.db");
                var recordingPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    "Blackbox");

                services.AddSingleton(new RecordingSettings { RecordingLocation = recordingPath });
                services.AddSingleton(new ObsProvisioningOptions
                {
                    PortableRootDirectory = Path.Combine(appData, "obs-portable"),
                    ConnectionSettingsPath = Path.Combine(appData, "obs-connection.json"),
                    MicrophoneSettingsPath = Path.Combine(appData, "microphone.json"),
                    AutomaticCaptureSettingsPath = Path.Combine(appData, "automatic-capture.json")
                });
                services.AddSingleton(new ObsOnboardingOptions());
                services.AddSingleton(new FfmpegOptions
                {
                    RootDirectory = Path.Combine(appData, "ffmpeg"),
                    WorkDirectory = Path.Combine(appData, "ffmpeg-work"),
                    TimelineCacheDirectory = Path.Combine(appData, "timeline-cache")
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
                services.AddSingleton(new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(20)
                });
                services.AddSingleton<IClock, SystemClock>();
                services.AddSingleton<IDiagnosticLogReader, DiagnosticLogReader>();
                services.AddSingleton<SupportBundleService>();
                services.AddSingleton<UserExperienceSettingsStore>();
                services.AddSingleton<IWindowsStartupManager, WindowsStartupManager>();
                services.AddSingleton<ISegmentRepository>(_ => new SqliteSegmentRepository(databasePath));
                services.AddSingleton<IGameProfileRepository>(_ => new SqliteGameProfileRepository(databasePath));
                services.AddSingleton<ISegmentUsageRegistry, SegmentUsageRegistry>();
                services.AddSingleton<IFfmpegProvisioner, FfmpegProvisioner>();
                services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
                services.AddSingleton<RecordingSessionCatalog>();
                services.AddSingleton<RecordingLibraryService>();
                services.AddSingleton<IFfmpegCommandRunner, FfmpegCommandRunner>();
                services.AddSingleton<SessionExportService>();
                services.AddSingleton<SessionPlaybackService>();
                services.AddSingleton<TimelineAssetService>();
                services.AddSingleton<RecordingRecoveryService>();
                services.AddSingleton<ObsSetupRequestBuilder>();
                services.AddSingleton<ObsConnectionSettingsProvider>();
                services.AddSingleton<IObsConnectionSettingsProvider>(provider =>
                    provider.GetRequiredService<ObsConnectionSettingsProvider>());
                services.AddSingleton<MicrophoneConfigurationStore>();
                services.AddSingleton<IMicrophoneConfigurationStore>(provider =>
                    provider.GetRequiredService<MicrophoneConfigurationStore>());
                services.AddSingleton<AutomaticCapturePreferenceStore>();
                services.AddSingleton<IAutomaticCapturePreferenceStore>(provider =>
                    provider.GetRequiredService<AutomaticCapturePreferenceStore>());
                services.AddSingleton<IObsInstallationLocator, ObsInstallationLocator>();
                services.AddSingleton<IObsPortableProvisioner, ObsPortableProvisioner>();
                services.AddSingleton<ObsWebSocketRpcClient>();
                services.AddSingleton<IObsWebSocketRpcClient>(provider =>
                    provider.GetRequiredService<ObsWebSocketRpcClient>());
                services.AddSingleton<IObsAudioMeterClient>(provider =>
                    provider.GetRequiredService<ObsWebSocketRpcClient>());
                services.AddSingleton<IObsController, ObsWebSocketController>();
                services.AddSingleton(new GpuActivityOptions());
                services.AddSingleton<IGpuActivityProbe, WindowsGpuActivityProbe>();
                services.AddSingleton<IRunningApplicationCatalog, WindowsRunningApplicationCatalog>();
                services.AddSingleton<IGameProcessDetector, WindowsGameProcessDetector>();
                services.AddSingleton<IObsMicrophoneController, ObsMicrophoneController>();
                services.AddSingleton(new MicrophoneMonitoringOptions());
                services.AddSingleton<IMicrophoneDeviceMonitor, MicrophoneDeviceMonitor>();
                services.AddSingleton<MicrophoneCalibrationAnalyzer>();
                services.AddSingleton<MicrophoneCalibrationService>();
                services.AddSingleton<RecordingCoordinator>();
                services.AddSingleton(new AutomaticCaptureOptions());
                services.AddSingleton<AutomaticCaptureController>();
                services.AddSingleton<AutomaticCaptureService>();
                services.AddSingleton<AudioConfigurationService>();
                services.AddSingleton<ObsSetupPlanner>();
                services.AddSingleton<ObsAutoSetupService>();
                services.AddSingleton<StartupRecoveryState>();
                services.AddSingleton<StartupRecoveryCoordinator>();
                services.AddSingleton<DiagnosticsService>();
                services.AddSingleton<ProtectionService>();
                services.AddSingleton<StorageQuotaEnforcer>();
                services.AddSingleton<GlobalHotkeyService>();
                services.AddSingleton<TrayIconService>();
                services.AddTransient<MicrophoneCalibrationWindow>();
                services.AddSingleton<Func<MicrophoneCalibrationWindow>>(provider =>
                    () => provider.GetRequiredService<MicrophoneCalibrationWindow>());
                services.AddTransient<RecordingLibraryWindow>();
                services.AddSingleton<Func<RecordingLibraryWindow>>(provider =>
                    () => provider.GetRequiredService<RecordingLibraryWindow>());
                services.AddTransient<GameProfilesWindow>();
                services.AddSingleton<Func<GameProfilesWindow>>(provider =>
                    () => provider.GetRequiredService<GameProfilesWindow>());
                services.AddTransient<DiagnosticsWindow>();
                services.AddSingleton<Func<DiagnosticsWindow>>(provider =>
                    () => provider.GetRequiredService<DiagnosticsWindow>());
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();
        await _host.Services.GetRequiredService<ISegmentRepository>().InitializeAsync();
        await _host.Services.GetRequiredService<IGameProfileRepository>().InitializeAsync();
        var experienceSettings = _host.Services
            .GetRequiredService<UserExperienceSettingsStore>()
            .Current;
        try
        {
            _host.Services
                .GetRequiredService<IWindowsStartupManager>()
                .SetEnabled(experienceSettings.StartWithWindows);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not refresh the Blackbox Windows startup registration.");
        }

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.StartHidden = e.Args.Any(static argument =>
            argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
        mainWindow.Show();
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

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
