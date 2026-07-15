# Milestone 3C Manual Test

This procedure tests one-click OBS onboarding with both existing-installation detection and download fallback.

## Prerequisites

- Windows 10 version 2004/build 19041 or later.
- An existing standard or Steam OBS installation, or an internet connection for the download fallback.
- Enough free disk space for the private OBS runtime. Blackbox uses local hard links when possible and copies files when required.
- A microphone if microphone source/filter behavior will be checked.

## Procedure

1. Run `dotnet build`.
2. Run `dotnet test` and confirm every test passes.
3. Run `dotnet run --project src\Blackbox.App\Blackbox.App.csproj`.
4. Confirm `Start Recording` and `Apply Audio` are disabled before setup.
5. Click `Setup OBS` once.
6. If OBS is already installed, confirm the status reports that OBS was found and prepared locally without downloading. Otherwise, confirm the status moves through download, verification, and extraction.
7. Confirm the status then moves through launch, connection, configuration, and recording-check stages.
8. Confirm the final status is `OBS is installed, configured, and ready.`
9. Confirm `%LOCALAPPDATA%\Blackbox\obs-portable\bin\64bit\obs64.exe` exists.
10. Confirm `%LOCALAPPDATA%\Blackbox\obs-portable\portable_mode.txt` exists.
11. Confirm a short MKV probe recording exists under `%USERPROFILE%\Videos\Blackbox`.
12. Confirm `Start Recording` and `Apply Audio` are enabled.
13. Click `Setup OBS` again and confirm it reuses the running backend without downloading another copy or failing on existing resources.
14. Click `Start Recording`, wait at least five seconds, and click `Stop`.
15. Confirm a new MKV file appears under `%USERPROFILE%\Videos\Blackbox`.
16. Open the private OBS window and confirm the `Blackbox` profile, `Blackbox` scene collection, and `Blackbox Recording` scene exist.
17. Confirm the scene contains game capture, game audio, voice chat, raw microphone, and processed microphone sources.
18. Confirm only the processed microphone has noise suppression, expander, compressor, and limiter filters.
19. Confirm track 2 is game audio, track 3 is voice chat, track 4 is raw microphone, and track 5 is processed microphone. The processed microphone also contributes to track 1.
20. Confirm the OBS recording profile uses MKV, 48 kHz audio, tracks 1 through 5, and time-based splitting at the configured segment duration.

## Expected Limitation

The isolated game and voice-chat sources do not yet select a process automatically. Their exact executable/window binding will be supplied by game detection and per-game profiles. Until then, the recording probe proves OBS startup, encoding, file output, source creation, filters, and track layout; it does not prove isolated game or Discord audio content.

## Troubleshooting

- Setup logs are written to `%LOCALAPPDATA%\Blackbox\logs`.
- A failed package checksum stops extraction and reports a verification error.
- A missing OBS source or filter type is reported by name.
- A rejected websocket operation reports the OBS request name, status code, and comment.
- If OBS starts but cannot become ready, confirm `plugin_config\obs-websocket\config.json` has `server_enabled` set to `true`; Blackbox rewrites this setting before every launch.
- Delete only `%LOCALAPPDATA%\Blackbox\obs-portable` when intentionally forcing clean provisioning. Blackbox will check local OBS installations again before using the download fallback. Keep recording files and `blackbox.db` intact.
