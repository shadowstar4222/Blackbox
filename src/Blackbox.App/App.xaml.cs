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
                var recordingPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    "Blackbox");

                services.AddSingleton(new RecordingSettings { RecordingLocation = recordingPath });
                services.AddSingleton(new ObsProvisioningOptions
                {
                    PortableRootDirectory = Path.Combine(appData, "obs-portable"),
                    ConnectionSettingsPath = Path.Combine(appData, "obs-connection.json"),
                    MicrophoneSettingsPath = Path.Combine(appData, "microphone.json")
                });
                services.AddSingleton(new ObsOnboardingOptions());
                services.AddSingleton(new FfmpegOptions
                {
                    RootDirectory = Path.Combine(appData, "ffmpeg"),
                    WorkDirectory = Path.Combine(appData, "ffmpeg-work"),
                    TimelineCacheDirectory = Path.Combine(appData, "timeline-cache")
                });
                services.AddSingleton(new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(20)
                });
                services.AddSingleton<IClock, SystemClock>();
                services.AddSingleton<ISegmentRepository>(_ => new SqliteSegmentRepository(Path.Combine(appData, "blackbox.db")));
                services.AddSingleton<ISegmentUsageRegistry, SegmentUsageRegistry>();
                services.AddSingleton<IFfmpegProvisioner, FfmpegProvisioner>();
                services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
                services.AddSingleton<RecordingSessionCatalog>();
                services.AddSingleton<RecordingLibraryService>();
                services.AddSingleton<IFfmpegCommandRunner, FfmpegCommandRunner>();
                services.AddSingleton<SessionExportService>();
                services.AddSingleton<SessionPlaybackService>();
                services.AddSingleton<TimelineAssetService>();
                services.AddSingleton<ObsSetupRequestBuilder>();
                services.AddSingleton<ObsConnectionSettingsProvider>();
                services.AddSingleton<IObsConnectionSettingsProvider>(provider =>
                    provider.GetRequiredService<ObsConnectionSettingsProvider>());
                services.AddSingleton<MicrophoneConfigurationStore>();
                services.AddSingleton<IMicrophoneConfigurationStore>(provider =>
                    provider.GetRequiredService<MicrophoneConfigurationStore>());
                services.AddSingleton<IObsInstallationLocator, ObsInstallationLocator>();
                services.AddSingleton<IObsPortableProvisioner, ObsPortableProvisioner>();
                services.AddSingleton<ObsWebSocketRpcClient>();
                services.AddSingleton<IObsWebSocketRpcClient>(provider =>
                    provider.GetRequiredService<ObsWebSocketRpcClient>());
                services.AddSingleton<IObsAudioMeterClient>(provider =>
                    provider.GetRequiredService<ObsWebSocketRpcClient>());
                services.AddSingleton<IObsController, ObsWebSocketController>();
                services.AddSingleton<IObsMicrophoneController, ObsMicrophoneController>();
                services.AddSingleton(new MicrophoneMonitoringOptions());
                services.AddSingleton<IMicrophoneDeviceMonitor, MicrophoneDeviceMonitor>();
                services.AddSingleton<MicrophoneCalibrationAnalyzer>();
                services.AddSingleton<MicrophoneCalibrationService>();
                services.AddSingleton<RecordingCoordinator>();
                services.AddSingleton<AudioConfigurationService>();
                services.AddSingleton<ObsSetupPlanner>();
                services.AddSingleton<ObsAutoSetupService>();
                services.AddSingleton<ProtectionService>();
                services.AddSingleton<StorageQuotaEnforcer>();
                services.AddSingleton<GlobalHotkeyService>();
                services.AddTransient<MicrophoneCalibrationWindow>();
                services.AddSingleton<Func<MicrophoneCalibrationWindow>>(provider =>
                    () => provider.GetRequiredService<MicrophoneCalibrationWindow>());
                services.AddTransient<RecordingLibraryWindow>();
                services.AddSingleton<Func<RecordingLibraryWindow>>(provider =>
                    () => provider.GetRequiredService<RecordingLibraryWindow>());
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();
        await _host.Services.GetRequiredService<ISegmentRepository>().InitializeAsync();
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
