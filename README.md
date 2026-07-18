# Blackbox

Blackbox is a Windows 10 64-bit continuous background gameplay recorder built with .NET 8, WPF, SQLite, OBS Studio, and FFmpeg.

Minimum supported OS: Windows 10 version 2004, build 19041.

## Milestone 7D Desktop Experience Status

Implemented:

- An OBS-inspired dark capture console with persistent navigation, compact status summaries, and responsive drawer panels.
- Games, microphone, diagnostics, and settings drawers that keep common actions in the main window.
- A dedicated Blackbox application icon for the executable, taskbar, title bars, and notification area.
- Notification-area controls for opening Blackbox, recording, protection, remembered-game watching, recordings, and exit.
- Optional close-to-notification-area and minimize-to-notification-area behavior.
- Optional Windows startup through the current-user Run key with a quiet `--background` launch.
- Optional remembered-game watching that starts the private OBS backend and capture detector after Blackbox launches.
- Atomically persisted desktop preferences with safe recovery from missing, corrupted, or failed writes.
- Dark themed timeline, game profile, microphone, and diagnostics windows with keyboard focus and 150% scaling fixes.
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
- An embedded Blackbox video player backed by bundled LibVLC codecs.
- Play/pause, 10-second jumps, segment jumps, frame stepping, speed, loop, volume, mute, audio-track, and fullscreen controls.
- Numbered segment bands, live scrubbing, quick tags, manual event markers, marker navigation, and marker removal.
- Full-session stream-copy export and accurate selected-range export to one MKV or MP4 while preserving readable isolated audio tracks.
- Per-track mute, solo, volume, WAV-selection controls, and common export presets.
- Optional 24-bit PCM WAV export for selected isolated audio tracks.
- Export progress, cancellation, atomic completion, and source-segment locks that prevent quota deletion during playback and export.
- A remembered-games manager populated from useful application windows that are currently running.
- Persistent per-game automatic-recording enablement keyed by full executable path.
- Opt-in automatic capture that ignores every executable the user has not remembered.
- Start-time OBS scene, canvas, game-video, and isolated game-audio rebinding using the live window identifier and client size.
- A forced source re-hook and short settle delay before recording begins, preventing stale blank captures.
- Two-sample launch confirmation and a 15-second stop grace period to avoid capture churn during startup and brief focus changes.
- Recording ownership that prevents automatic capture from stopping a manually started recording.
- Startup media recovery that probes stable MKV and MP4 files, attempts a lossless FFmpeg remux when needed, and never replaces the original unless the repaired file passes verification.
- Recovery backups that preserve the original damaged file and any useful failed repair output for manual inspection.
- Startup reconciliation that imports surviving recordings, marks damaged media clearly, and repairs missing-file database state.
- Active-file protection that detects changing OBS output and skips it during recovery.
- Surviving OBS-session adoption after a Blackbox crash, including restoration of the working Stop control without interrupting recording.
- Persisted automatic-capture intent so an interrupted automatic session can resume after Blackbox restarts.
- An in-app diagnostics window with recording state, automatic-capture state, indexed-media health, storage use, recovery results, and categorized recent logs.
- A privacy-reviewed local support bundle with capped diagnostic events and automatic redaction of credentials, user-profile paths, URI credentials, and microphone identifiers.
- Explicit exclusion of recordings, screenshots, databases, OBS passwords/settings, microphone configuration, game profiles, executable lists, and application settings from support bundles.
- Migration-safe per-game aliases plus audio, launcher-handoff, and GPU-preference settings.
- Automatic launcher-child discovery that requires two consecutive detections before remembering the final executable as an alias.
- Same-process window replacement detection so OBS re-hooks when a launcher changes its capture window without changing executables.
- Optional Windows GPU activity corroboration that ranks likely game windows without requiring administrator access or process injection.
- Live GPU utilization in the running-applications picker and removable executable aliases in each remembered profile.
- Atomic settings writes, bounded diagnostic logs, bounded timeline caches, serialized tool provisioning, and SQLite WAL concurrency.
- Reduced startup and library work by reusing healthy media metadata and skipping unnecessary FFmpeg provisioning and probes.
- Deterministic cancellation and shutdown for automatic capture, microphone monitoring, recording, playback leases, hotkeys, and WPF windows.
- Hardened OBS websocket message limits, native DLL search paths, setup readiness retries, and portable-runtime installation.

Automatic capture now binds game video and optional isolated game audio only after a remembered game or verified launcher child starts. Milestone 7D desktop quality-of-life work is complete.

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
2. Use the `Capture` workspace and click `Setup OBS`.
3. Wait while Blackbox finds an existing OBS installation or downloads the official package, then starts its private OBS runtime.
4. Confirm the status changes to `OBS is installed, configured, and ready.`
5. Open the `Microphone` drawer to apply routing or calibrate the selected microphone.
6. Use `Start Recording` and `Stop` for recording tests.
7. Use `Recordings` to browse the visual timeline, open the full Blackbox player from any cursor position, tag moments, select a range, mix tracks, and export one continuous video.
8. Start a game, open the `Games` drawer and game manager, select it under running applications, and remember it.
9. Open `Settings` and enable `Watch remembered games` to let Blackbox start and stop recording with that game.
10. Use `Open Folder` when you need direct access to the underlying safe segments.
11. Open the `Diagnostics` drawer and workspace to inspect recovery results, media health, storage use, and recent recording or detection events.
12. Use `Support bundle` only when troubleshooting. Review the privacy disclosure, choose a local ZIP destination, and inspect the ZIP before sharing it.
13. Optionally enable `Start with Windows`; Blackbox then starts quietly in the notification area.

The OBS onboarding procedure is in `docs/obs-test-setup.md`. The Milestone 4 microphone procedure is in `docs/milestone-4-microphone-test.md`. The continuous-session export procedure is in `docs/milestone-5-continuous-export-test.md`. The complete automatic-capture procedure is in `docs/milestone-6a-automatic-capture-test.md`. The crash-recovery procedure is in `docs/milestone-6c-recovery-diagnostics-test.md`. The Milestone 7 audit is in `docs/milestone-7-hardening-report.md`. The desktop quality-of-life validation is in `docs/milestone-7d-desktop-experience-test.md`.

## Current Milestone

Milestone 7D's OBS-inspired shell, drawers, icons, background startup, notification-area controls, and remembered-game watching are complete. The OBS dock edition remains planned as Milestone 8. See `docs/roadmap.md` for the acceptance criteria.
