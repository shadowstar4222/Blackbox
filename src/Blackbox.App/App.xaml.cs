using System.Windows;
using Blackbox.Domain;
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
                services.AddSingleton<IClock, SystemClock>();
                services.AddSingleton<ISegmentRepository>(_ => new SqliteSegmentRepository(Path.Combine(appData, "blackbox.db")));
                services.AddSingleton<IObsController, ObsWebSocketController>();
                services.AddSingleton<RecordingCoordinator>();
                services.AddSingleton<AudioConfigurationService>();
                services.AddSingleton<ObsSetupPlanner>();
                services.AddSingleton<ObsAutoSetupService>();
                services.AddSingleton<ProtectionService>();
                services.AddSingleton<StorageQuotaEnforcer>();
                services.AddSingleton<GlobalHotkeyService>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();
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
