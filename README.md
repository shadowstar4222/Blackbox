# Blackbox

Record the Windows games and applications you choose, review a continuous
timeline, and export sessions or clips as one video. **OBS is not required.**

**Windows 10 (build 19041+) and Windows 11, 64-bit.**

[Download Blackbox 1.0.0](https://github.com/shadowstar4222/Blackbox/releases/download/v1.0.0/Blackbox-v1.0.0-win-x64.zip)
| [Release notes](https://github.com/shadowstar4222/Blackbox/releases/tag/v1.0.0)

**1.0.0 is the native-recorder release**, including the clearer playback controls
from preview.18. The [previous OBS version](https://github.com/shadowstar4222/Blackbox/releases/tag/v1.0.0-obs-legacy)
is preserved separately. Test under your normal workload before relying on a long recording.

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
- In-app review with pictorial frame/segment controls, 10-second jumps, markers, speed, loop, and volume controls.
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

See the [native guide](docs/NATIVE-RECORDER.txt), [current release details](docs/RELEASE-1.0.0-NATIVE.txt),
[validation history](docs/native-recorder-validation.md), and [roadmap](docs/roadmap.md).
The release-details file covers packaging requirements and known limitations.

## License

[GPL-3.0-or-later](LICENSE). Dependencies retain their own
[licenses and notices](src/Blackbox.App/THIRD-PARTY-NOTICES.txt).
