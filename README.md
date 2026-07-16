# Blackbox

Blackbox is a Windows 10 64-bit continuous background gameplay recorder built with .NET 8, WPF, SQLite, OBS Studio, and FFmpeg.

Minimum supported OS: Windows 10 version 2004, build 19041.

## Milestone 6A Status

Implemented:

- Recording coordination, completed-segment discovery, and SQLite metadata from Milestone 1.
- Quota pruning, missing-file reconciliation, five-minute protection, and the `Ctrl+Shift+F7` hotkey from Milestone 2.
- Five-track audio routing, processed microphone filters, and microphone level calculations from Milestone 3.
- One selected Windows microphone routed to separate raw and processed OBS sources.
- Live three-phase microphone calibration for room noise, normal voice, and loud voice.
- Persisted input-gain, expander, compressor, and limiter recommendations with clipping and automatic-gain warnings.
- Before/after comparison recordings with direct open controls.
- Recording-time device monitoring that preserves source timing during disconnects and restores the selected device after reconnection.
- Direct `Open Recordings` access from the main window.
- One-click OBS provisioning that first detects standard and Steam installations, including secondary Steam libraries.
- A private portable runtime prepared from local OBS files when available, with the official OBS GitHub release used only as a fallback.
- SHA-256 package verification when the official release publishes a digest.
- User-writable portable OBS storage under `%LOCALAPPDATA%\Blackbox\obs-portable`; no administrator access is required.
- Private localhost websocket authentication on a dynamically selected port.
- Automatic websocket-server enablement and repair before each private OBS launch.
- Persisted connection settings so a running Blackbox OBS instance is reused after an app restart.
- Idempotent creation of the Blackbox profile, scene collection, scene, sources, filters, and track assignments.
- OBS response validation with readable per-request failure messages.
- MKV recording, tracks 1 through 5, 48 kHz audio, time-based file splitting configuration, and profile reload after output-mode changes.
- A short first-run recording probe that must produce a real output file before setup succeeds.
- WPF setup progress and recording controls that remain disabled until OBS passes setup.
- Startup database initialization and contained UI command failures so feature errors do not terminate the app.
- Automated coverage for provisioning, connection reuse, protocol responses, repeat setup, recording configuration, storage, protection, and audio models.
- A recordings library that backfills completed MKV and MP4 files into SQLite and groups compatible adjacent segments into continuous sessions.
- An integrated timeline with cached thumbnails, a full-mix waveform, continuous seeking, and playback from the cursor.
- Durable markers and protected ranges plus clear damaged-media and timeline-gap reporting.
- Continuous playback through an automatically provisioned local FFmpeg toolset.
- Full-session stream-copy export and accurate selected-range export to one MKV or MP4 while preserving readable isolated audio tracks.
- Per-track mute, solo, volume, WAV-selection controls, and common export presets.
- Optional 24-bit PCM WAV export for selected isolated audio tracks.
- Export progress, cancellation, atomic completion, and source-segment locks that prevent quota deletion during playback and export.
- Opt-in automatic capture for foreground Steam games detected from their library path or Steam process ancestry.
- Exact OBS game-video and isolated game-audio rebinding to the detected window.
- Two-sample launch confirmation and a 15-second stop grace period to avoid capture churn during startup and brief focus changes.
- Recording ownership that prevents automatic capture from stopping a manually started recording.

Automatic capture now binds the game video and game-audio sources for foreground Steam games. Voice-chat selection, non-Steam executable profiles, GPU corroboration, and per-game overrides remain in Milestone 6B.

## Build

```powershell
dotnet restore
dotnet build
dotnet test
```

Run the WPF app:

```powershell
dotnet run --project src\Blackbox.App\Blackbox.App.csproj
```

## First Run

1. Start Blackbox.
2. Click `Setup OBS`.
3. Wait while Blackbox finds an existing OBS installation or downloads the official package, then starts its private OBS runtime.
4. Confirm the status changes to `OBS is installed, configured, and ready.`
5. Use `Calibrate Mic` to select and tune the microphone.
6. Use `Start Recording` and `Stop` for recording tests.
7. Use `Recordings` to browse the visual timeline, play from any cursor position, select a range, mix tracks, and export one continuous video.
8. Click `Enable Auto`, switch to a running Steam game, and let Blackbox start and stop recording with the game.
9. Use `Open Folder` when you need direct access to the underlying safe segments.

The OBS onboarding procedure is in `docs/obs-test-setup.md`. The Milestone 4 microphone procedure is in `docs/milestone-4-microphone-test.md`. The continuous-session export procedure is in `docs/milestone-5-continuous-export-test.md`. The automatic-capture procedure is in `docs/milestone-6a-automatic-capture-test.md`.

## Current Milestone

Milestone 6A now provides the first automatic Steam-game capture path. Milestone 6B adds configured executable and GPU signals plus persistent per-game profiles; Milestone 6C adds crash recovery and diagnostics. Hardening/optimization and an OBS dock edition are planned as Milestones 7 and 8. See `docs/roadmap.md` for the acceptance criteria.
