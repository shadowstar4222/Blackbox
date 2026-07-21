<p align="center">
  <img src="src/Blackbox.App/Assets/Blackbox.png" alt="Blackbox application icon" width="112" height="112">
</p>

# Blackbox

Blackbox is a Windows recorder that automatically captures games and application windows through a private OBS Studio backend. It records resilient short segments, shows them as one continuous timeline, and exports them as one video when you are ready.

**Windows 10 or later | 64-bit | Current release: v1.0.0**

[Download Blackbox v1.0.0](https://github.com/shadowstar4222/Blackbox/releases/tag/v1.0.0)

## Features

- Automatically records only the games and applications you choose to remember.
- Sets up and controls a private OBS runtime without changing your personal OBS scenes.
- Captures exact game windows and adjusts when a window is resized.
- Switches between multiple open remembered games, even during a recording.
- Records separate game, voice-chat, raw-microphone, and processed-microphone tracks.
- Follows the current Windows default microphone with exclusions and manual selection.
- Presents segmented recordings as one continuous video timeline.
- Includes playback controls, frame stepping, markers, tags, protected ranges, and segment boundaries.
- Exports complete sessions or selected ranges as one MKV or MP4.
- Recovers and diagnoses interrupted recordings while preserving the original files.
- Runs from the notification area and can start automatically with Windows.

## Install

1. Open the [v1.0.0 release](https://github.com/shadowstar4222/Blackbox/releases/tag/v1.0.0).
2. Download `Blackbox-v1.0.0-win-x64.zip` and its SHA-256 checksum file.
3. Extract the entire ZIP to a normal writable folder.
4. Run `Blackbox.App.exe`.
5. Wait until Blackbox reports that OBS is configured and ready.
6. Make a short manual test recording before enabling automatic capture.

The package is self-contained and does not require a separate .NET installation. Blackbox uses an existing standard or Steam OBS installation when available, otherwise it downloads an official private runtime. Administrator access is not required.

Minimum supported system: Windows 10 version 2004, build 19041, on 64-bit Windows.

## First Setup

1. Open **Microphone** and confirm the selected input or exclusions.
2. Use **Check OBS** to validate the managed OBS setup with a short recording.
3. Start a game or application and leave its taskbar window open.
4. Open **Games**, then **Manage remembered games**.
5. Select the running application and choose **Remember as game**.
6. Enable automatic capture from the Capture workspace or Settings.

When multiple remembered games are open, select the intended running game and choose **Use for capture**. Blackbox keeps that game preferred until it closes.

## Recordings

Recordings are stored under:

```text
%USERPROFILE%\Videos\Blackbox
```

Manual recordings use `Manual\YYYY-MM-DD`. Automatic recordings use `Application Name\YYYY-MM-DD`.

Blackbox records short MKV segments for crash resistance and presents compatible segments as one continuous session. The default audio layout is:

| Track | Audio |
| --- | --- |
| 1 | Full listening mix |
| 2 | Game audio |
| 3 | Voice chat |
| 4 | Raw microphone |
| 5 | Processed microphone |

Use **Protect 5 min** or `Ctrl+Shift+F7` to keep recent footage from automatic cleanup.

## Build From Source

Requirements: Windows 10 or later and the .NET 8 SDK.

```powershell
dotnet restore Blackbox.sln
dotnet build Blackbox.sln -c Release --no-restore
dotnet test Blackbox.sln -c Release --no-build
dotnet run --project src\Blackbox.App\Blackbox.App.csproj -c Release
```

## Privacy

- Recordings and application state stay on the local computer.
- Blackbox does not require a cloud account or upload recordings.
- OBS websocket access is authenticated and bound to localhost.
- Blackbox does not inject code into games, Steam, or voice-chat processes.
- Support bundles exclude recordings, screenshots, databases, OBS passwords, saved profiles, executable lists, microphone configuration, and settings files.

## Help And Documentation

- Use the in-app **Help** and **Diagnostics** workspaces first.
- Read [`BLACKBOX-DETAILS.txt`](BLACKBOX-DETAILS.txt) for the complete manual, troubleshooting, storage policy, architecture, safety model, and contributor notes.
- Read [`docs/roadmap.md`](docs/roadmap.md) for completed milestones and planned work.
- Logs are stored under `%LOCALAPPDATA%\Blackbox\logs`.

## Release Status

Blackbox v1.0.0 has been validated on Windows 10 x64:

- Release build: zero warnings and zero errors.
- Automated tests: 150 passed, zero failed, zero skipped.
- Private OBS startup, exact-window capture, automatic microphone routing, continuous playback/export, recovery, and remembered-game switching verified.

The next planned milestone is an OBS dock edition.

## License

Blackbox is free and open-source software licensed under the [GNU General Public License version 3 or later](LICENSE). You may use, study, modify, and redistribute it under those terms. Distributed modified versions must remain under the GPL, provide corresponding source, preserve the license, copyright, and [original-project attribution](NOTICE), and clearly identify their changes.

Third-party components remain under their own licenses. See [`src/Blackbox.App/THIRD-PARTY-NOTICES.txt`](src/Blackbox.App/THIRD-PARTY-NOTICES.txt) for bundled LibVLC notices. OBS Studio and FFmpeg remain subject to their respective licenses.
