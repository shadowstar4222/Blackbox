# Blackbox

Blackbox is a Windows 10 64-bit continuous background gameplay recorder built with .NET 8, WPF, SQLite, OBS Studio, and FFmpeg.

Minimum supported OS: Windows 10 version 2004, build 19041.

## Milestone 1 Status

Implemented:

- Solution structure split into app, domain, infrastructure, recording, storage, export, and test projects.
- WPF shell with start/stop recording controls.
- OBS controller abstraction and placeholder `ObsWebSocketController` for a dedicated portable OBS profile.
- Recording coordinator that validates settings, initializes SQLite, configures segmented recording, and starts/stops OBS.
- SQLite segment repository for completed segment metadata.
- Completed MKV segment scanner that imports stable segment files once.
- Automated unit/integration tests for settings, coordinator sequencing, SQLite persistence, and segment scanning.

The concrete obs-websocket protocol calls are intentionally isolated behind `IObsController`; replacing the placeholder adapter is the next Milestone 1 hardening task before real capture use.

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

## Manual Test Procedure: Milestone 1

1. Install OBS Studio on Windows 10 build 19041 or later.
2. Create a dedicated portable OBS directory for Blackbox.
3. Build the solution with `dotnet build`.
4. Run `dotnet test` and confirm all tests pass.
5. Start the WPF app.
6. Press `Start Recording`.
7. Confirm `%LOCALAPPDATA%\Blackbox\blackbox.db` is created.
8. Confirm `%USERPROFILE%\Videos\Blackbox` is created.
9. Press `Stop`.
10. Review `%LOCALAPPDATA%\Blackbox\logs` for structured start/stop records.

Until the real obs-websocket adapter is completed, this procedure validates application orchestration, state persistence setup, and the OBS control boundary rather than actual media capture.
