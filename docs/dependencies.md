# Dependencies

## Runtime

- Windows 10 version 2004, build 19041 or later.
- .NET 8 Desktop Runtime, included in the portable package.
- Windows Graphics Capture, Media Foundation, and the x64 Visual C++ runtime bundled with the worker.
- FFmpeg and FFprobe, included for local muxing, probing, timeline assets, recovery, and export. FFplay is no longer shipped or required.
- SQLite via `Microsoft.Data.Sqlite`.
- Windows `RegisterHotKey` through `user32.dll` for global hotkeys.
- Windows top-level-window APIs plus the Toolhelp process snapshot API for the non-invasive running-game picker.
- Windows PDH (`pdh.dll`) GPU Engine performance counters for optional per-process activity corroboration.
- NAudio WASAPI capture and resampling for the native 48 kHz microphone path.
- SpeexDSP noise suppression through SpeexDSPSharp for native microphone processing.
- LibVLCSharp.WPF and one lazily initialized VideoLAN runtime for in-app recording review; hardware decoding is requested when available.

## NuGet

- `Microsoft.Data.Sqlite`: SQLite persistence.
- `Microsoft.Extensions.DependencyInjection`: dependency injection.
- `Microsoft.Extensions.Hosting`: app host lifecycle.
- `Microsoft.Extensions.Logging.Abstractions`: logging contracts in non-UI projects.
- `Serilog.Extensions.Hosting`: structured logging integration.
- `Serilog.Sinks.File`: rolling diagnostic logs.
- `NAudio`: Windows microphone capture, PCM conversion, metering, and filtering.
- `SpeexDSPSharp`: managed SpeexDSP preprocessor bindings and native noise suppression.
- `ScreenRecorderLib`: Windows Graphics Capture and Media Foundation recording worker.
- `xunit`: automated tests.

The deferred `Blackbox.Dock` project depends on ASP.NET Core and retains its own
tests. It is excluded from the native desktop application and portable package.
Older OBS integration source remains for the deferred dock and compatibility
tests; the current application does not register an OBS recorder or installer.
