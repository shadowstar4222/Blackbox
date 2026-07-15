# Blackbox

Blackbox is a Windows 10 64-bit continuous background gameplay recorder built with .NET 8, WPF, SQLite, OBS Studio, and FFmpeg.

Minimum supported OS: Windows 10 version 2004, build 19041.

## Milestone 3C Status

Implemented:

- Recording coordination, completed-segment discovery, and SQLite metadata from Milestone 1.
- Quota pruning, missing-file reconciliation, five-minute protection, and the `Ctrl+Shift+F7` hotkey from Milestone 2.
- Five-track audio routing, processed microphone filters, and microphone level calculations from Milestone 3.
- One-click OBS provisioning that first detects standard and Steam installations, including secondary Steam libraries.
- A private portable runtime prepared from local OBS files when available, with the official OBS GitHub release used only as a fallback.
- SHA-256 package verification when the official release publishes a digest.
- User-writable portable OBS storage under `%LOCALAPPDATA%\Blackbox\obs-portable`; no administrator access is required.
- Private localhost websocket authentication on a dynamically selected port.
- Automatic websocket-server enablement and repair before each private OBS launch.
- Persisted connection settings so a running Blackbox OBS instance is reused after an app restart.
- Idempotent creation of the Blackbox profile, scene collection, scene, sources, filters, and track assignments.
- OBS response validation with readable per-request failure messages.
- MKV recording, tracks 1 through 5, 48 kHz audio, and time-based file splitting configuration.
- A short first-run recording probe that must produce a real output file before setup succeeds.
- WPF setup progress and recording controls that remain disabled until OBS passes setup.
- Startup database initialization and contained UI command failures so feature errors do not terminate the app.
- Automated coverage for provisioning, connection reuse, protocol responses, repeat setup, recording configuration, storage, protection, and audio models.

Game and voice-chat audio sources are created during setup, but selecting their exact executable/window is not automatic yet. That binding will be added with game detection and per-game profiles; the current setup probe validates the backend and recording structure.

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
5. Use `Start Recording` and `Stop` for recording tests.

The complete Milestone 3C manual procedure and troubleshooting steps are in `docs/obs-test-setup.md`.
