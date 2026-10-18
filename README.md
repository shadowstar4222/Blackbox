# Blackbox

Record the Windows games and applications you choose, review a continuous
timeline, and export sessions or clips as one video. **OBS is not required.**

**Windows 10 (build 19041+) and Windows 11, 64-bit.**

[Download preview.17](https://github.com/shadowstar4222/Blackbox/releases/download/v2.0.0-preview.17/Blackbox-v2.0.0-preview.17-win-x64.zip)
| [Release notes](https://github.com/shadowstar4222/Blackbox/releases/tag/v2.0.0-preview.17)

This is a **2.0 testing preview**, not a stable release. The older v1.0.0 download
uses OBS; choose preview.17 for the built-in recorder.

## Get Started

1. Extract the **entire ZIP** and run `Blackbox.App.exe`.
2. Open your game, then use **Games** to select its window, **Remember**, and **Capture**.
3. Press **Record**, or **Enable auto** for remembered applications.
4. Choose your microphone and voice preset under **Microphone**, then **Apply**.
5. Open **Recordings** to review, mark, protect, clip, or export your session.

Use **Settings** for recording quality, Windows startup, and automatic watching.
Closing to the tray keeps Blackbox running; use its tray menu to exit.

## What It Does

- Built-in window capture with hardware-requested H.264 recording and recoverable MKV segments.
- Remembered applications, window switching, and automatic recording.
- Microphone routing, exclusions, calibration, noise reduction, EQ, compression, and limiting.
- In-app review with frame stepping, 10-second jumps, markers, speed, loop, and volume controls.
- Continuous review of compatible completed MKVs, one-video export, and buffered time-shift.
- Compact themed controls, recording health/audio feedback, and an optional recording-setup window.

## Before You Record

- Windows 10 records the system-output mix plus your microphone, so other playing apps may be included. Native recording currently produces **one stereo mix**, not isolated tracks.
- Hardware encoding can fall back to software. Test under your normal game load; capture-blocking games may need borderless/windowed mode. Blackbox does not bypass capture protections.
- Time-shift follows completed segments, not a zero-delay feed. Helldivers 2, other GPUs, and long sessions still need broader testing.
- The optional setup scene contains bright colors and motion. Review its photosensitivity warning before starting.

Recordings stay in `%USERPROFILE%\Videos\Blackbox`, grouped by application and
date. Settings and logs stay in `%LOCALAPPDATA%\Blackbox`. No account or video
upload is required. Updating the portable folder preserves that local data.

## Build And Details

Build on Windows with the .NET 8 SDK:

```powershell
dotnet build Blackbox.sln -c Release
dotnet test Blackbox.sln -c Release
```

See the [native guide](docs/NATIVE-RECORDER.txt), [current release details](docs/RELEASE-2.0.0-preview.17.txt),
[validation history](docs/native-recorder-validation.md), and [roadmap](docs/roadmap.md).
The release-details file covers packaging requirements and known limitations.

## License

[GPL-3.0-or-later](LICENSE). Dependencies retain their own
[licenses and notices](src/Blackbox.App/THIRD-PARTY-NOTICES.txt).
