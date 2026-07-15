# Dependencies

## Runtime

- Windows 10 version 2004, build 19041 or later.
- .NET 8 Desktop Runtime.
- OBS Studio with obs-websocket support, provisioned as a private portable copy by Blackbox on first use.
- Internet access during first-time OBS provisioning.
- FFmpeg for clip export in later milestones.
- SQLite via `Microsoft.Data.Sqlite`.
- Windows `RegisterHotKey` through `user32.dll` for global hotkeys.
- OBS Application Audio Capture for isolated per-process audio sources.

## NuGet

- `Microsoft.Data.Sqlite`: SQLite persistence.
- `Microsoft.Extensions.DependencyInjection`: dependency injection.
- `Microsoft.Extensions.Hosting`: app host lifecycle.
- `Microsoft.Extensions.Logging.Abstractions`: logging contracts in non-UI projects.
- `Serilog.Extensions.Hosting`: structured logging integration.
- `Serilog.Sinks.File`: rolling diagnostic logs.
- `xunit`: automated tests.

## Planned

- Windows process/GPU activity discovery.
- Audio device monitoring.
- FFmpeg process execution and progress parsing.
