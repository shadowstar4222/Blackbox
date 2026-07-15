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

## In Progress

### Milestone 5: Continuous Sessions, Timeline, And One-File Export

The segmented MKV files remain the safe recording format on disk, but the application must hide those boundaries during normal use.

- [x] Group related segments into recording sessions and display each session as one continuous video.
- [x] Provide a recordings library that selects the latest session by default.
- [x] Play continuously across segment boundaries.
- [x] Show duration, recording time, segment count, and missing media or timeline gaps without silently skipping them.
- [ ] Add integrated seeking and scrubbing with thumbnail previews.
- [ ] Show markers, protected ranges, and damaged-media details on the visual timeline.
- [ ] Generate thumbnails and audio waveforms asynchronously.
- [x] Support start/end range selection plus a full-session shortcut.
- [x] Join every compatible segment in the selected range into one output file using FFmpeg.
- [x] Use stream copy for a full compatible session and re-encode accurately trimmed selections.
- [x] Preserve synchronization and readable names for the full mix, game, voice chat, raw microphone, and processed microphone tracks.
- [x] Export one MKV or editor-compatible MP4.
- [ ] Add optional separate WAV files for chosen audio tracks.
- [ ] Add track mute, solo, and volume controls plus export presets.
- [x] Show export progress, cancellation, completion, and contained failure details.
- [x] Lock source segments against quota deletion while playback or export is using them.

Milestone 5 is complete only when a multi-segment test session can be viewed as one recording and exported as one playable file with matching duration and synchronized audio.

### Milestone 6: Automatic Capture And Recovery

- Detect games from foreground, GPU-active, configured, and Steam process information.
- Add per-game recording profiles.
- Recover incomplete media and reconcile state after crashes.
- Add recording and export diagnostics.
