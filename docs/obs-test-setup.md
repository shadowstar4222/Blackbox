# Milestone 3C Manual Test

This procedure tests one-click OBS onboarding. Do not install or configure OBS manually first.

## Prerequisites

- Windows 10 version 2004/build 19041 or later.
- An internet connection for the first setup.
- At least 1 GB of temporary free disk space for the OBS archive, extraction, and portable copy.
- A microphone if microphone source/filter behavior will be checked.

## Procedure

1. Run `dotnet build`.
2. Run `dotnet test` and confirm every test passes.
3. Run `dotnet run --project src\Blackbox.App\Blackbox.App.csproj`.
4. Confirm `Start Recording` and `Apply Audio` are disabled before setup.
5. Click `Setup OBS` once.
6. Confirm the status moves through download, verification, extraction, launch, connection, configuration, and recording-check stages.
7. Confirm the final status is `OBS is installed, configured, and ready.`
8. Confirm `%LOCALAPPDATA%\Blackbox\obs-portable\bin\64bit\obs64.exe` exists.
9. Confirm `%LOCALAPPDATA%\Blackbox\obs-portable\portable_mode.txt` exists.
10. Confirm a short MKV probe recording exists under `%USERPROFILE%\Videos\Blackbox`.
11. Confirm `Start Recording` and `Apply Audio` are enabled.
12. Click `Setup OBS` again and confirm it reuses the running backend without downloading another copy or failing on existing resources.
13. Click `Start Recording`, wait at least five seconds, and click `Stop`.
14. Confirm a new MKV file appears under `%USERPROFILE%\Videos\Blackbox`.
15. Open the private OBS window and confirm the `Blackbox` profile, `Blackbox` scene collection, and `Blackbox Recording` scene exist.
16. Confirm the scene contains game capture, game audio, voice chat, raw microphone, and processed microphone sources.
17. Confirm only the processed microphone has noise suppression, expander, compressor, and limiter filters.
18. Confirm track 2 is game audio, track 3 is voice chat, track 4 is raw microphone, and track 5 is processed microphone. The processed microphone also contributes to track 1.
19. Confirm the OBS recording profile uses MKV, 48 kHz audio, tracks 1 through 5, and time-based splitting at the configured segment duration.

## Expected Limitation

The isolated game and voice-chat sources do not yet select a process automatically. Their exact executable/window binding will be supplied by game detection and per-game profiles. Until then, the recording probe proves OBS startup, encoding, file output, source creation, filters, and track layout; it does not prove isolated game or Discord audio content.

## Troubleshooting

- Setup logs are written to `%LOCALAPPDATA%\Blackbox\logs`.
- A failed package checksum stops extraction and reports a verification error.
- A missing OBS source or filter type is reported by name.
- A rejected websocket operation reports the OBS request name, status code, and comment.
- Delete only `%LOCALAPPDATA%\Blackbox\obs-portable` when intentionally forcing a clean OBS download. Keep recording files and `blackbox.db` intact.
