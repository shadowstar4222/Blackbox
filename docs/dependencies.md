# Dependencies

## Runtime

- Windows 10 version 2004, build 19041 or later.
- .NET 8 Desktop Runtime.
- OBS Studio with obs-websocket support, provisioned as a private portable runtime by Blackbox on first use.
- An existing standard or Steam OBS installation, or internet access for first-time download fallback.
- FFmpeg, FFprobe, and FFplay, automatically downloaded and checksum-verified on first recordings-library use.
- SQLite via `Microsoft.Data.Sqlite`.
- Windows `RegisterHotKey` through `user32.dll` for global hotkeys.
- OBS Application Audio Capture for isolated per-process audio sources.
- OBS websocket input-volume events and Windows Audio Capture device properties for calibration and connection monitoring.

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
