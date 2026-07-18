# Architecture

Blackbox uses OBS Studio as the capture and encoding backend while keeping all product state, storage policy, and export decisions in the Blackbox service layer.

## Projects

- `Blackbox.App`: WPF desktop shell, embedded LibVLC player, dependency injection, logging setup, system-tray and full UI entry point.
- `Blackbox.Domain`: shared entities, settings, and repository abstractions.
- `Blackbox.Infrastructure`: external integrations such as OBS control and future Windows process/device discovery.
- `Blackbox.Recording`: recording orchestration, segment import, session management, and future hotkey/game-detection workflows.
- `Blackbox.Storage`: SQLite persistence and future storage quota enforcement.
- `Blackbox.Export`: recording-library indexing, FFmpeg provisioning and probing, continuous-session playback, and export orchestration.
- `Blackbox.Tests`: unit and practical integration tests.

## Milestone Flow

1. UI or background policy requests recording start.
2. `RecordingCoordinator` validates `RecordingSettings`.
3. SQLite metadata schema is initialized.
4. `IObsController` launches/configures OBS with MKV segmentation.
5. OBS writes short independent MKV segments.
6. `SegmentScanner` imports completed stable MKV files into SQLite.
7. `ProtectionService` marks recent overlapping rows as protected when the user presses a protection command.
8. `StorageQuotaEnforcer` reconciles missing files, then deletes oldest unprotected rows until quota policy is satisfied.
9. `AudioConfigurationService` validates isolated app audio assignments and microphone processing before forwarding them to OBS.
10. `ObsInstallationLocator` searches standard install paths, the Steam registry, and every Steam library for an existing OBS installation.
11. `ObsPortableProvisioner` prepares an isolated portable runtime from local OBS files when possible, otherwise downloads and verifies the official release.
12. `ObsAutoSetupService` reuses a running private connection when possible, otherwise creates a private localhost websocket connection and launches OBS.
13. `ObsWebSocketController` creates or reuses the Blackbox OBS resources and configures MKV splitting, five tracks, and 48 kHz audio.
14. `ObsWebSocketRpcClient` identifies with obs-websocket v5 and validates every single or batched response.
15. Setup records a short probe and succeeds only when OBS returns an output path that exists on disk.
16. `MicrophoneCalibrationService` captures live OBS meter events, calculates processing recommendations, and persists one device for both microphone paths.
17. `MicrophoneDeviceMonitor` watches the selected device only while recording, leaves OBS sources alive during disconnects, and reapplies the saved configuration after reconnection.
18. Milestone 5 groups segments into a virtual continuous session for seamless browsing and playback without replacing the interruption-resistant source files.
19. `PlaybackWindow` maps one global playhead across the physical segments, switches media at boundaries, and provides frame, seek, speed, audio, marker, and fullscreen controls through bundled LibVLC.
20. `TimelineAssetService` generates and caches codec-safe thumbnails plus a full-mix waveform while holding source-segment leases.
21. Timeline markers and protected ranges are stored in SQLite; protected selections also mark overlapping segments against quota deletion.
22. Full-session and selected-range exports use FFmpeg to produce one MKV or MP4 with the chosen track mix and optional separate 24-bit PCM WAV files.
23. Playback and export acquire in-memory segment leases so quota enforcement cannot remove source media in use.
24. FFmpeg, FFprobe, and FFplay are downloaded over HTTPS on first library use, checksum-verified, and staged under Blackbox application data.
25. `WindowsRunningApplicationCatalog` enumerates visible top-level windows, executable paths, live client sizes, and OBS window identifiers without injecting into another process.
26. `WindowsRunningApplicationCatalog` includes bounded process ancestry for visible windows without opening invasive handles or injecting into another process.
27. `WindowsGpuActivityProbe` batches Windows PDH GPU-engine samples for candidate process IDs and returns an optional ranking signal.
28. `WindowsGameProcessDetector` matches primary paths and aliases, ranks foreground and GPU-active windows, and learns a launcher child only after two consecutive matches.
29. `AutomaticCaptureService` confirms stable remembered candidates, resets and fits the OBS game sources, then starts or stops through the shared `RecordingCoordinator`.
30. The coordinator serializes manual and automatic lifecycle requests so automatic capture never stops a recording it did not start.
31. `RecordingRecoveryService` probes stable recording files during startup, attempts a lossless FFmpeg remux for unreadable media, verifies the repair, and atomically replaces the source while preserving the original in the recovery-backups folder.
32. Startup recovery skips files whose size or write time is still changing, then refreshes the recording library to reconcile SQLite rows and retain clear damage labels.
33. `RecordingCoordinator` queries OBS recording status and adopts a surviving recording after a Blackbox restart without issuing another start request.
34. `AutomaticCapturePreferenceStore` atomically persists automatic-capture intent so startup can resume an interrupted automatic session after OBS is ready.
35. `DiagnosticLogReader` reads rolling Serilog files with shared access and classifies recent system, recording, detection, export, and recovery events for the diagnostics window.
36. `UserExperienceSettingsStore` atomically persists Windows startup, notification-area, and remembered-game watching preferences.
37. `WindowsStartupManager` owns the current-user Run registration and always launches the same executable with `--background`.
38. `TrayIconService` mirrors recording, OBS, game, and automatic-capture state and routes notification-area commands back through the main dispatcher.
39. `MainWindow` remains the lifetime owner while hidden, so startup recovery, capture detection, protection, and recording continue without a taskbar window.

## Safety Boundaries

- Blackbox does not inject into Steam, games, or voice-chat processes.
- OBS interaction is isolated behind `IObsController`.
- Portable OBS is stored under Blackbox app data and does not modify the user's normal or Steam OBS profile.
- OBS release archives use HTTPS and are checked against the release SHA-256 digest when one is published.
- Segment files are imported only after they are stable and non-empty.
- Deletion logic validates database state, skips protected footage, and removes a database row only after the file is gone.
- WPF targets `net8.0-windows` and stays within Windows 10-compatible UI technology.
- The embedded player uses the official LibVLCSharp wrapper and bundled VideoLAN Windows runtime; package versions and LGPL notices ship with the app.
- Raw microphone and processed microphone are modeled as separate tracks so the raw path remains non-destructive.
- Microphone disconnects do not remove either OBS source, preserving silence and timeline alignment until the selected device returns.
- Missing or discontinuous session media is shown as unhealthy and is rejected by continuous playback and export rather than silently skipped.
- Damaged files remain visible in the library with their probe failure instead of disappearing from recording history.
- Timeline assets are generated in a staging directory and moved into a keyed local cache only after successful completion.
- Exports write video and WAV outputs to unique partial paths and publish them only after FFmpeg exits successfully with non-empty files.
- Automatic capture is disabled until the user enables it after a successful OBS check.
- Automatic detection requires an enabled profile explicitly remembered from the running-applications picker; unapproved applications are ignored.
- Launch confirmation and a stop grace period prevent brief process or focus transitions from repeatedly starting and stopping OBS.
- Launcher-child aliases require two consecutive detections before they are persisted and remain visible and removable in the Games window.
- GPU activity is a ranking signal, not a hard recording dependency; executable and foreground matching continue when counters are unavailable.
- Game-audio capture can be disabled per profile, in which case Blackbox mutes and deactivates the isolated OBS game-audio source.
- Recovery operates only on stable files and leaves a changing OBS output untouched.
- Repaired media replaces its source only after FFprobe validates a non-empty staged output; the original is retained as a recovery backup.
- A failed remux leaves the source byte-for-byte unchanged and preserves any non-empty diagnostic output separately.
- Automatic-capture preference writes use temporary files and atomic moves so a process interruption cannot leave partial JSON state.
- Windows startup is opt-in, current-user only, and does not require administrator access.
- Closing to the notification area never stops an active recording or remembered-game detector; explicit `Exit Blackbox` uses the guarded shutdown path.

## Database

The `segments` table stores one row per completed segment, including session, time range, game identity, video format, audio track layout, encoder, resolution, frame rate, HDR flag, protection and damage state, path, and size. The `game_profiles` table stores remembered executable paths, JSON aliases, display names, automatic-recording enablement, game-audio preference, launcher-handoff preference, GPU-ranking preference, and timestamps.

The `timeline_markers` and `protected_ranges` tables store durable user annotations against session wall-clock time. Existing databases are migrated in place when new damage columns or timeline tables are introduced.

Future tables:

- sessions
- clips
- devices
- export_jobs
- diagnostics
