# Blackbox Roadmap

## Completed

### Milestone 1: Recording Foundation

- Launch and control OBS.
- Start and stop recording.
- Create segmented MKV files.
- Detect completed segments and store their metadata in SQLite.

### Milestone 2: Retention And Protection

- Apply storage quotas.
- Delete the oldest unprotected segments.
- Protect the previous five minutes with a global hotkey.

### Milestone 3: Isolated Audio

- Record game, voice chat, raw microphone, and processed microphone paths separately.
- Add microphone filters and level metering.

### Milestone 4: Microphone Calibration And Resilience

- Persist one microphone across the raw and processed paths.
- Handle disconnect and reconnect without shortening either audio track.
- Measure room noise, normal voice, and loud voice.
- Recommend gain and filter thresholds and record a comparison.

### Milestone 5: Continuous Sessions, Timeline, And One-File Export

The segmented MKV files remain the safe recording format on disk, but the application must hide those boundaries during normal use.

- [x] Group related segments into recording sessions and display each session as one continuous video.
- [x] Provide a recordings library that selects the latest session by default.
- [x] Play continuously across segment boundaries.
- [x] Replace external playback with an embedded Blackbox player that keeps the complete session in one review surface.
- [x] Add play, pause, start/end, 10-second jumps, segment jumps, frame stepping, speed, loop, volume, mute, audio-track, and fullscreen controls.
- [x] Draw numbered segment bands and durable marker pins directly on the playback scrub bar.
- [x] Add quick tags, manual event labels, marker navigation, and marker removal at the live playback position.
- [x] Show duration, recording time, segment count, and missing media or timeline gaps without silently skipping them.
- [x] Add integrated seeking and scrubbing with thumbnail previews.
- [x] Show markers, protected ranges, and damaged-media details on the visual timeline.
- [x] Generate thumbnails and audio waveforms asynchronously and cache them between launches.
- [x] Support start/end range selection plus a full-session shortcut.
- [x] Join every compatible segment in the selected range into one output file using FFmpeg.
- [x] Use stream copy for a full compatible session and re-encode accurately trimmed selections.
- [x] Preserve synchronization and readable names for the full mix, game, voice chat, raw microphone, and processed microphone tracks.
- [x] Export one MKV or editor-compatible MP4.
- [x] Add optional separate WAV files for chosen audio tracks.
- [x] Add track mute, solo, and volume controls plus export presets.
- [x] Show export progress, cancellation, completion, and contained failure details.
- [x] Lock source segments against quota deletion while playback or export is using them.

Milestone 5 is complete only when a multi-segment test session can be viewed as one recording and exported as one playable file with matching duration and synchronized audio.

The advanced player pass was validated against a real three-segment Blackbox session. LibVLC opened the MKV media, decoded 30 fps video, exposed six audio choices, accepted seeking and playback-rate changes, and the WPF player opened, played, and closed without leaking its segment lease.

### Milestone 6: Automatic Capture And Recovery

#### Milestone 6A: Detection And Automatic Recording

- [x] Enumerate useful running game windows without process injection.
- [x] Bind OBS game video, isolated game audio, canvas size, and scene fit when the game starts.
- [x] Debounce game launches and delay automatic stop across short exits or focus transitions.
- [x] Keep automatic capture opt-in and preserve manual recording ownership.
- [x] Add optional GPU-activity corroboration for difficult launcher and window cases.

#### Milestone 6B: Per-Game Profiles

- [x] Persist remembered executables and per-game automatic-recording enablement.
- [x] Add a taskbar-window picker with profile enable, disable, and remove controls.
- [x] Add additional capture preferences and executable aliases per game.
- [x] Rebind capture when a launcher hands off to the final game process.

#### Milestone 6C: Recovery And Diagnostics

- [x] Recover incomplete media and reconcile state after crashes.
- [x] Preserve recoverable footage and label unrecoverable files clearly.
- [x] Add recording, detection, and export diagnostics.

Milestone 6C was validated by force-closing Blackbox during a real OBS recording. OBS continued writing, startup skipped the active file, Blackbox adopted the surviving recording, and the recovered session stopped cleanly from the app.

Milestone 6 is complete. The final live pass measured a running Steam game through Windows GPU counters, persisted its GPU preference, bound OBS with `GpuActivity` corroboration, recorded isolated game audio, and stopped cleanly through automatic capture.

### Milestone 7: Debugging, Hardening, And Optimization

- [x] Add an in-app diagnostics workspace with support-bundle export and privacy review.
- [x] Profile startup, recording, timeline generation, memory, disk IO, and export performance.
- [x] Bound caches and background work, reduce unnecessary media probes, and add stress tests.
- [x] Add repeated reliability, low-disk-style, device-loss, cancellation, and failure-injection test passes.
- [x] Resolve accessibility, usability, and Windows 10 compatibility findings before release.

Milestone 7 is complete. The final pass audited all source and test projects, added bounded diagnostic and timeline storage, removed unnecessary media probes, hardened asynchronous shutdown and native/OBS resource ownership, made settings writes atomic, enabled SQLite WAL concurrency, and added a privacy-reviewed support bundle.

The Release build has zero warnings and errors, all 114 automated tests pass, five final full-suite repetitions passed consecutively, and high-priority production findings from the exhaustive .NET analyzer pass are clear. A live Windows 10 test launched StarVester through Steam, detected its remembered executable and window, configured OBS at the live canvas size, recorded 95.433 seconds of nonblank H.264 video with five AAC tracks, and stopped automatically after the game exited. A separate cold-start run proved one-click OBS setup waits through backend initialization and completes its probe recording.

See `docs/milestone-7-hardening-report.md` for the audit and validation record.

### Milestone 7D: Desktop Experience And Quality Of Life

- [x] Replace the utility-style main window with an OBS-inspired capture console.
- [x] Add persistent navigation and animated Games, Microphone, Diagnostics, and Settings drawers.
- [x] Add a Blackbox executable, taskbar, title-bar, and notification-area icon.
- [x] Add notification-area recording, protection, automatic-capture, recordings, open, and exit actions.
- [x] Add optional close-to-notification-area and minimize-to-notification-area behavior.
- [x] Add optional current-user Windows startup with a quiet background launch.
- [x] Start the private OBS backend and remembered-game detector automatically when watching is enabled.
- [x] Persist desktop preferences atomically and recover safe defaults from corrupted settings.
- [x] Apply the dark desktop theme to recordings, game profiles, microphone calibration, and diagnostics.
- [x] Validate keyboard focus, drawers, 150% display scaling, background launch, and startup registration.

Milestone 7D is complete. The Release build has zero warnings and errors, all 121 automated tests pass, and the desktop workflow was exercised against the private OBS runtime on Windows 10. See `docs/milestone-7d-desktop-experience-test.md`.

### Milestone 7E: Capture Quality Of Life

- [x] Prepare the private OBS backend automatically when Blackbox starts, without making a probe recording.
- [x] Add persisted resolution, frame-rate, and audio-bitrate controls.
- [x] Make automatic recording at startup a clear Settings option.
- [x] Organize manual and automatic recordings by application and local recording date.
- [x] Index and recover recordings recursively across the organized folder layout.
- [x] Reframe OBS when the captured window changes size without interrupting recording.
- [x] Preserve full setup checks as an explicit action in their own internal folder.

Milestone 7E is complete. The Release build has zero warnings and errors, all 138 automated tests pass, and a live startup prepared OBS without creating a probe file. A live manual test produced a 16.05-second 1920 x 1080, 60 fps H.264 recording with five AAC tracks under `Manual\2026-07-18`. See `docs/milestone-7e-capture-qol-test.md`.

### Milestone 7F: Capture Reliability And Onboarding

- [x] Pin every active game capture to `Capture specific window` and `Window title must match`.
- [x] Preserve OBS anti-cheat compatibility while rebinding the exact live game window.
- [x] Recover protected Steam game executable paths from active Steam PID tracking.
- [x] Route the current Windows default microphone during setup and recording start.
- [x] Add microphone exclusions and an explicit manual-selection mode.
- [x] Reapply microphone routing when the Windows default endpoint changes.
- [x] Add a first-run tutorial, permanent Help workspace, guided setup actions, tooltips, and control reference.

Milestone 7F is complete. The Release build has zero warnings and errors and all 144 automated tests pass. The protected-game parser was validated against this machine's real Helldivers 2 Steam process entries, and live startup confirmed exact OBS title matching plus Windows-default microphone routing. See `docs/milestone-7f-reliability-onboarding-test.md`.

### Milestone 7G: Active Game Switching

- [x] List the remembered state of every running taskbar application in the Games window.
- [x] Add an explicit `Use for capture` action for any running remembered game.
- [x] Persist the preferred remembered profile and exact executable across Blackbox restarts.
- [x] Rank the preferred game above foreground and GPU heuristics while it remains open.
- [x] Fall back to another eligible remembered game when the preferred game closes.
- [x] Rebind OBS video and isolated game audio without stopping an active recording.
- [x] Prepare the selected OBS target while idle and preserve normal automatic-start confirmation.
- [x] Show the active and preferred capture state directly in the running-application list.

Milestone 7G is complete. The Release build has zero warnings and errors and all 150 automated tests pass. See `docs/milestone-7g-game-switching-test.md`.

## Planned

### Milestone 8: OBS Dock Edition

- Build an OBS custom browser dock that exposes Blackbox recording status and common controls.
- Reuse the same localhost service and authenticated control contracts as the desktop app.
- Provide timeline launch, protection, marker, microphone, and automatic-capture controls in OBS.
- Package one-click dock registration without modifying the user's normal OBS scenes or profiles.
