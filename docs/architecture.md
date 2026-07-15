# Architecture

Blackbox uses OBS Studio as the capture and encoding backend while keeping all product state, storage policy, and export decisions in the Blackbox service layer.

## Projects

- `Blackbox.App`: WPF desktop shell, dependency injection, logging setup, system-tray and full UI entry point.
- `Blackbox.Domain`: shared entities, settings, and repository abstractions.
- `Blackbox.Infrastructure`: external integrations such as OBS control and future Windows process/device discovery.
- `Blackbox.Recording`: recording orchestration, segment import, session management, and future hotkey/game-detection workflows.
- `Blackbox.Storage`: SQLite persistence and future storage quota enforcement.
- `Blackbox.Export`: FFmpeg export orchestration in later milestones.
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
10. `ObsPortableProvisioner` downloads, verifies, extracts, and launches a user-writable portable OBS copy when one is not already available.
11. `ObsAutoSetupService` reuses a running private connection when possible, otherwise creates a private localhost websocket connection and launches OBS.
12. `ObsWebSocketController` creates or reuses the Blackbox OBS resources and configures MKV splitting, five tracks, and 48 kHz audio.
13. `ObsWebSocketRpcClient` identifies with obs-websocket v5 and validates every single or batched response.
14. Setup records a short probe and succeeds only when OBS returns an output path that exists on disk.
15. Later milestones bind detected games to application-audio inputs, browse the timeline, and export segments.

## Safety Boundaries

- Blackbox does not inject into Steam, games, or voice-chat processes.
- OBS interaction is isolated behind `IObsController`.
- Portable OBS is stored under Blackbox app data and does not modify the user's normal OBS profile.
- OBS release archives use HTTPS and are checked against the release SHA-256 digest when one is published.
- Segment files are imported only after they are stable and non-empty.
- Deletion logic validates database state, skips protected footage, and removes a database row only after the file is gone.
- WPF targets `net8.0-windows` and stays within Windows 10-compatible UI technology.
- Raw microphone and processed microphone are modeled as separate tracks so the raw path remains non-destructive.

## Database

Milestone 1 stores one row per completed segment, including session, time range, game identity, video format, audio track layout, encoder, resolution, frame rate, HDR flag, protection flag, path, and size.

Future tables:

- sessions
- markers
- protected_ranges
- clips
- devices
- game_profiles
- export_jobs
- diagnostics
