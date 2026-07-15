# Dependencies

## Runtime

- Windows 10 version 2004, build 19041 or later.
- .NET 8 Desktop Runtime.
- OBS Studio with obs-websocket support.
- FFmpeg for clip export in later milestones.
- SQLite via `Microsoft.Data.Sqlite`.
- Windows `RegisterHotKey` through `user32.dll` for global hotkeys.

## NuGet

- `Microsoft.Data.Sqlite`: SQLite persistence.
- `Microsoft.Extensions.DependencyInjection`: dependency injection.
- `Microsoft.Extensions.Hosting`: app host lifecycle.
- `Microsoft.Extensions.Logging.Abstractions`: logging contracts in non-UI projects.
- `Serilog.Extensions.Hosting`: structured logging integration.
- `Serilog.Sinks.File`: rolling diagnostic logs.
- `xunit`: automated tests.

## Planned

- obs-websocket client library or a small first-party WebSocket JSON-RPC adapter.
- Windows process/GPU activity discovery.
- Audio device monitoring.
- FFmpeg process execution and progress parsing.
