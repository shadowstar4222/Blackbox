# Milestone 7F Reliability And Onboarding Validation

Date: 2026-07-21  
Host: Windows 10 Home 22H2, build 19045, x64

## Automated Validation

- Release build: zero warnings and zero errors.
- Full Release test suite: 144 passed, zero failed, zero skipped.
- Focused capture, Steam process, microphone selection, persistence, and tutorial-preference suite: 18 passed.
- OBS request tests assert `capture_mode=window`, the exact live window identifier, `priority=1`, and the anti-cheat hook.
- Microphone tests cover Windows-default selection, excluded defaults, manual selection, and settings persistence.
- Steam process tests cover the real quoted Helldivers 2 command format and removal after process exit.

## Live Validation

1. Started the Release build with the private OBS backend stopped.
2. Confirmed Blackbox launched the managed portable OBS runtime and waited through websocket readiness retries.
3. Confirmed startup setup completed without a probe recording.
4. Confirmed the OBS scene stored `capture_mode=window`, the current game window identifier, and `priority=1` (`Window title must match`).
5. Confirmed Blackbox resolved the Windows default capture endpoint as `Digital Audio Interface (Valve VR Radio & HMD Mic)`.
6. Confirmed both raw and processed OBS microphone paths were configured while the prior calibration settings remained intact.
7. Confirmed the Release WPF process remained responsive after startup and automatic audio routing.

Windows Graphics Capture could not image the WPF window on this host because the capture helper returned `No such interface supported (0x80004002)`. XAML compilation, handler wiring, startup behavior, process responsiveness, and persistence were verified; visual screenshot validation remains a manual packaging check.

## Helldivers 2 Evidence

The installed Steam app manifest identifies app ID `553850` and the executable at `steamapps\common\Helldivers 2\bin\helldivers2.exe`. Steam's real process log records that executable as a tracked game PID. The new fallback parses the active mapping only while Steam considers the process running and verifies that the recovered filename matches the actual process snapshot before exposing the taskbar window.
