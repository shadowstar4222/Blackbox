# Blackbox

Blackbox is a Windows 10 64-bit continuous background gameplay recorder built with .NET 8, WPF, SQLite, OBS Studio, and FFmpeg.

Minimum supported OS: Windows 10 version 2004, build 19041.

## Milestone 3 Status

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
- Audio routing profile for full mix, game, voice chat, raw microphone, and processed microphone tracks.
- Executable-name audio assignment model for isolated application categories.
- Microphone processing settings for noise suppression, expander, compressor, and limiter controls.
- Microphone level meter calculations for peak and RMS dBFS snapshots.
- OBS audio configuration boundary connected to the recording start pipeline and WPF `Apply Audio` action.
- Automatic OBS setup milestone foundation with OBS connection settings, setup plan generation, and WPF `Setup OBS` action.
- OBS websocket RPC client with v5 identify/auth handling and request batching for setup, recording, and audio commands.
- Automated unit/integration tests for settings, coordinator sequencing, SQLite persistence, segment scanning, quota pruning, protection, audio routing, microphone filters, and level metering.

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

OBS setup for manual testing is documented in `docs/obs-test-setup.md`.

## Manual Test Procedure: Milestone 3

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
15. Follow `docs/obs-test-setup.md` to enable OBS websocket.
16. Press `Setup OBS` and confirm the app validates the Blackbox setup plan.
17. Press `Apply Audio` and confirm the app validates the Blackbox five-track routing profile.
18. In OBS, confirm track 1 is the full mix, track 2 game audio, track 3 voice chat, track 4 raw microphone, and track 5 processed microphone.
19. Confirm the raw microphone source has no destructive filters and the processed microphone source has noise suppression, expander, compressor, and limiter.

The websocket transport is implemented, but OBS request names/source kinds may still need adjustment against the exact OBS version and plugins installed on your machine.
