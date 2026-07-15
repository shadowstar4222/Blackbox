# Blackbox

Blackbox is a Windows 10 64-bit continuous background gameplay recorder built with .NET 8, WPF, SQLite, OBS Studio, and FFmpeg.

Minimum supported OS: Windows 10 version 2004, build 19041.

## Milestone 2 Status

Implemented:

- Solution structure split into app, domain, infrastructure, recording, storage, export, and test projects.
- WPF shell with start/stop recording controls.
- OBS controller abstraction and placeholder `ObsWebSocketController` for a dedicated portable OBS profile.
- Recording coordinator that validates settings, initializes SQLite, configures segmented recording, and starts/stops OBS.
- SQLite segment repository for completed segment metadata.
- Completed MKV segment scanner that imports stable segment files once.
- Storage quota settings for maximum storage, maximum retained duration, recording location, and minimum free space.
- Storage pruning service that deletes oldest unprotected segments first.
- Database/file reconciliation when segment files are manually deleted outside the app.
- Five-minute protection service that marks overlapping existing segments without re-encoding.
- WPF actions for protecting the previous five minutes and applying quotas.
- Windows 10-compatible global hotkey registration for protecting the previous five minutes with `Ctrl+Shift+F7`.
- Automated unit/integration tests for settings, coordinator sequencing, SQLite persistence, segment scanning, quota pruning, and protection.

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

## Manual Test Procedure: Milestone 2

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
11. Add test segment rows through the repository tests or a local harness.
12. Press `Protect 5 Min` or `Ctrl+Shift+F7` and confirm overlapping segment rows have `is_protected = 1`.
13. Press `Apply Quotas` and confirm oldest unprotected files are deleted before newer or protected files.
14. Manually remove a known segment file, apply quotas, and confirm the missing row is reconciled from SQLite.

Until the real obs-websocket adapter is completed, this procedure validates application orchestration, state persistence setup, and the OBS control boundary rather than actual media capture.
