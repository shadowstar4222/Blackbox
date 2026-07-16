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

## In Progress

### Milestone 6: Automatic Capture And Recovery

#### Milestone 6A: Detection And Automatic Recording

- [x] Enumerate useful running game windows without process injection.
- [x] Bind OBS game video, isolated game audio, canvas size, and scene fit when the game starts.
- [x] Debounce game launches and delay automatic stop across short exits or focus transitions.
- [x] Keep automatic capture opt-in and preserve manual recording ownership.
- [ ] Add optional GPU-activity corroboration for difficult launcher and window cases.

#### Milestone 6B: Per-Game Profiles

- [x] Persist remembered executables and per-game automatic-recording enablement.
- [x] Add a running-application picker with profile enable, disable, and remove controls.
- [ ] Add additional capture preferences and executable aliases per game.
- [ ] Rebind capture when a launcher hands off to the final game process.

#### Milestone 6C: Recovery And Diagnostics

- [x] Recover incomplete media and reconcile state after crashes.
- [x] Preserve recoverable footage and label unrecoverable files clearly.
- [x] Add recording, detection, and export diagnostics.

Milestone 6C was validated by force-closing Blackbox during a real OBS recording. OBS continued writing, startup skipped the active file, Blackbox adopted the surviving recording, and the recovered session stopped cleanly from the app.

## Planned

### Milestone 7: Debugging, Hardening, And Optimization

- Add an in-app diagnostics workspace with support-bundle export and privacy review.
- Profile startup, recording, timeline generation, memory, disk IO, and export performance.
- Bound caches and background work, reduce unnecessary media probes, and add stress tests.
- Add long-running reliability, low-disk, device-loss, and failure-injection test passes.
- Resolve accessibility, usability, and Windows 10 compatibility findings before release.

### Milestone 8: OBS Dock Edition

- Build an OBS custom browser dock that exposes Blackbox recording status and common controls.
- Reuse the same localhost service and authenticated control contracts as the desktop app.
- Provide timeline launch, protection, marker, microphone, and automatic-capture controls in OBS.
- Package one-click dock registration without modifying the user's normal OBS scenes or profiles.
