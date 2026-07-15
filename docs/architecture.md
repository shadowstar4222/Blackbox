# Architecture

Blackbox uses OBS Studio as the capture and encoding backend while keeping all product state, storage policy, and export decisions in the Blackbox service layer.

## Projects

- `Blackbox.App`: WPF desktop shell, dependency injection, logging setup, system-tray and full UI entry point.
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
19. `TimelineAssetService` generates and caches codec-safe thumbnails plus a full-mix waveform while holding source-segment leases.
20. Timeline markers and protected ranges are stored in SQLite; protected selections also mark overlapping segments against quota deletion.
21. Full-session and selected-range exports use FFmpeg to produce one MKV or MP4 with the chosen track mix and optional separate 24-bit PCM WAV files.
22. Playback and export acquire in-memory segment leases so quota enforcement cannot remove source media in use.
23. FFmpeg, FFprobe, and FFplay are downloaded over HTTPS on first library use, checksum-verified, and staged under Blackbox application data.
24. Later milestones bind detected games to application-audio inputs and add crash recovery.

## Safety Boundaries

- Blackbox does not inject into Steam, games, or voice-chat processes.
- OBS interaction is isolated behind `IObsController`.
- Portable OBS is stored under Blackbox app data and does not modify the user's normal or Steam OBS profile.
- OBS release archives use HTTPS and are checked against the release SHA-256 digest when one is published.
- Segment files are imported only after they are stable and non-empty.
- Deletion logic validates database state, skips protected footage, and removes a database row only after the file is gone.
- WPF targets `net8.0-windows` and stays within Windows 10-compatible UI technology.
- Raw microphone and processed microphone are modeled as separate tracks so the raw path remains non-destructive.
- Microphone disconnects do not remove either OBS source, preserving silence and timeline alignment until the selected device returns.
- Missing or discontinuous session media is shown as unhealthy and is rejected by continuous playback and export rather than silently skipped.
- Damaged files remain visible in the library with their probe failure instead of disappearing from recording history.
- Timeline assets are generated in a staging directory and moved into a keyed local cache only after successful completion.
- Exports write video and WAV outputs to unique partial paths and publish them only after FFmpeg exits successfully with non-empty files.

## Database

The `segments` table stores one row per completed segment, including session, time range, game identity, video format, audio track layout, encoder, resolution, frame rate, HDR flag, protection and damage state, path, and size.

The `timeline_markers` and `protected_ranges` tables store durable user annotations against session wall-clock time. Existing databases are migrated in place when new damage columns or timeline tables are introduced.

Future tables:

- sessions
- clips
- devices
- game_profiles
- export_jobs
- diagnostics
