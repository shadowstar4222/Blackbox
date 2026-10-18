# Blackbox Roadmap

## Current Priority: Self-Contained Native Recording

Native Blackbox 1.0.0 supersedes the previous OBS version (v1.0.0-obs-legacy). Earlier
completed milestones describe the legacy backend, not a promise that all of its
audio/DSP capabilities exist in the native version.

- [x] Isolated Windows capture worker and bundled video tools; no OBS startup.
- [x] Exact window identity, cached taskbar discovery, encoded-progress checks.
- [x] GPU-requested H.264, continuous stream-copy segmentation, resize/pause handling.
- [x] System/process audio plus microphone mix; default microphone and exclusions.
- [x] Native broadcast microphone DSP, presets, calibration, and per-game profiles.
- [x] Finalized-segment ingestion and interrupted-recording recovery.
- [x] Live resize/minimize/restore/switch/closed-window/parent-disconnect smoke tests.
- [x] Cross-game session boundaries, automatic resumption after game closure,
      incremental segment accounting, and packaged manual-recording UI test.
- [x] Main-window review for sessions, clips, and exports; remove the external
      player dependency and reuse one lazy decoding runtime.
- [x] Indexed timeline lookup, cached timeline drawing, coalesced seeks, and
      asynchronous decoder release when review is closed or hidden.
- [x] Delayed time-shift review of finalized segments while capture continues.
- [x] Repeatable current-workload 720p30/1080p60/1440p60/4K30 worker matrix,
      in-app playback metrics, allocation profiling, and pipe failure tests.
- [ ] Profile and bound native active-capture memory commitment; managed memory
      stayed small in diagnostics, but native commitment reached about 702 MiB.
- [ ] True low-latency capture preview and exact-frame review cache with bounded
      resource budgets; measure recording plus playback under real game load.
- [ ] Game-load benchmarks and confirmed encoder reporting across NVIDIA/AMD/Intel.
- [ ] Live Helldivers 2 capture validation, protected-window and device-loss matrix.
- [ ] Native isolated audio tracks and automatic audio markers.
- [x] Themed pictorial seek controls, compact timeline layout, and pixel scrolling.
- [x] Fresh-source build/tests, portable ZIP integrity, and public-release packaging review.
- [ ] Long-duration soak testing across game and hardware configurations.

See [native recorder notes](NATIVE-RECORDER.txt) and the
[current-workload benchmark report](BENCHMARK-20261013.txt). OBS dock work
remains deferred. Synthetic-window measurements are not real-game FPS tests.

## Product Direction: Capture, Find, Shape

The compact workspace is the foundation: persistent recording controls, command
search, preview-first capture, and searchable sessions. New work should reduce
the time from recording a moment to finding and using it. See
[product direction](product-direction.md) for sequencing and concrete workflows.

- [x] Compact native shell, persistent recording strip, and searchable quick actions.
- [x] Filter sessions by application/date/marker text, today, protection, and health;
      sort by newest, oldest, or longest.
- [ ] Add saved views and local transcript search before adding new dashboard panels.
- [ ] Build a recoverable highlight/export queue around existing clips and markers.
- [ ] Add reversible native multitrack audio and per-session mixing.

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
- [x] Add persistent in-place navigation for Games, Microphone, Diagnostics, Help, and Settings.
- [x] Add a Blackbox executable, taskbar, title-bar, and notification-area icon.
- [x] Add notification-area recording, protection, automatic-capture, recordings, open, and exit actions.
- [x] Add optional close-to-notification-area and minimize-to-notification-area behavior.
- [x] Add optional current-user Windows startup with a quiet background launch.
- [x] Start the private OBS backend and remembered-game detector automatically when watching is enabled.
- [x] Persist desktop preferences atomically and recover safe defaults from corrupted settings.
- [x] Apply the dark desktop theme to recordings, game profiles, microphone calibration, and diagnostics.
- [x] Validate keyboard focus, in-place workspaces, 150% display scaling, background launch, and startup registration.

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

## Current Development

### Milestone 8: OBS Dock Edition

- [x] Build an OBS custom browser dock that exposes Blackbox recording status and common controls.
- [x] Reuse the desktop recording path through an authenticated, loopback-only control service.
- [x] Provide timeline launch, protection, live marker, microphone, and automatic-capture controls in OBS.
- [x] Package one-click dock registration without modifying the user's normal OBS scenes or profiles.
- [x] Persist live dock markers until completed segments can place them on a recording timeline.
- [x] Preserve unrelated custom docks and fail closed when existing OBS dock JSON is malformed.

The first Milestone 8 implementation is complete in the development source. The dock uses an unguessable local capability URL, no remote assets, restrictive browser headers, and Blackbox's managed portable `user.ini` only. See `docs/milestone-8-obs-dock-test.md`.

## Planned Development

### Milestone 9: Recording Guardian And Capture Preview

Blackbox should continuously prove that the intended video and audio are being captured, then attempt a bounded repair before a bad session is lost.

- [x] Show a low-overhead live capture preview and the exact active window, resolution, frame rate, and audio-track state.
- [x] Detect sustained black frames, frozen or stale video, a missing capture source, and unexpected silence on configured audio tracks.
- [ ] Distinguish expected silence and loading screens from failures with configurable thresholds and confirmation windows.
- [x] Rebind, resize, and refit the exact game source automatically, with a retry budget that cannot loop forever or interrupt healthy recordings.
- [x] Preserve the current recording while a safe repair is possible and clearly report when operator action is required.
- [x] Add notifications for recording start and stop, capture switches, microphone changes, low storage, detected failures, successful repairs, and unrecoverable faults.
- [x] Keep a guardian history in Diagnostics with the evidence, action, result, and active profile for every intervention.
- [x] Add failure-injection tests for black, frozen, missing-source, silent-track, resize, reconnect, and OBS-restart scenarios.

The first Milestone 9 implementation is complete in the development source. The remaining Milestone 9 work is a user-facing sensitivity editor and extended live soak testing against varied loading screens and intentionally quiet game scenes. See `docs/milestone-9-recording-guardian-test.md`.

Milestone 9 is complete when a long-running automatic session can detect simulated video and audio failures, repair recoverable capture faults without losing the session, and present trustworthy visual proof of what Blackbox is recording.

### Milestone 10: Game Profiles 2.0 And Input Controls

- [ ] Add per-game resolution, frame rate, encoder and quality, audio routing, microphone processing, crop, capture delay, storage retention, and export presets.
- [x] Separate inherited global defaults from explicit per-game overrides and provide a one-action reset to defaults.
- [x] Show the effective profile before recording and record the applied profile with session metadata.
- [ ] Add configurable keyboard and controller shortcuts for markers, protection, instant clips, capture switching, microphone mute, and recording control.
- [ ] Detect shortcut conflicts, support press and hold where useful, and keep destructive actions opt-in.
- [ ] Add per-game notification preferences so routine events can be quiet while capture failures remain visible.

The first Milestone 10 slice is complete in the development source. Per-game
resolution, frame rate, audio bitrate, segment length, storage retention,
capture delay, microphone routing and processing, and crop are applied during
automatic capture. Profiles are versioned, isolated, resettable, previewed
before recording, and written into session metadata. The input model now
validates keyboard/controller conflicts, press and hold actions, and explicit
recording-control opt-in. Live input registration, encoder execution, export
defaults, notification filtering, and their final editors remain open.

Milestone 10 is complete when two simultaneously installed games can use visibly different capture, audio, retention, export, notification, and input-control profiles without changing global settings or each other's recordings.

### Milestone 11: Instant Clips, Automatic Markers, And Session Notes

- [x] Save the previous 30 seconds, 2 minutes, 5 minutes, or a custom duration as an instant clip.
- [x] Let markers include configurable footage before and after the event while preserving the original segmented session.
- [x] Add a clip inbox with pending, ready, protected, exported, and failed states.
- [x] Add notes to complete sessions, individual markers, instant clips, and selected timeline ranges.
- [ ] Create optional automatic markers from audio spikes, scene changes, controller or keyboard actions, and user-defined rules.
- [ ] Make automatic markers explain why they were created and allow each detector to be tuned or disabled per game.
- [x] Protect source segments until every dependent clip or marker range has been finalized or explicitly discarded.

The first Milestone 11 implementation is complete in the development source.
Instant clips are durable background jobs, discover newly closed OBS segments
without opening the library, export atomically, and link back to their exact
source range. Marker context and notes persist in SQLite, while quota cleanup
honors pending clip ranges and marker-owned segments. Per-game automatic-marker
presets currently support audio spikes and major scene changes with stored
reasons and cooldowns. Keyboard/controller rule execution and a fine-grained
threshold editor remain open.

Milestone 11 is complete when a user can mark an event after it happens, receive a correctly bounded clip with before-and-after context, annotate it, and find its source range without manually joining segments.

### Milestone 12: Recording Search, Local Transcription, And Storage Intelligence

- [x] Basic application/date/marker search, Today/Protected/Needs attention filters,
      and newest/oldest/longest sorting in the native recordings page.
- [ ] Search and filter recordings by game, application, date, duration, tag, note, marker, protected state, export state, transcript text, and media health.
- [ ] Add calendar, recent-session, favorites, and saved-filter views.
- [ ] Add opt-in, on-device speech transcription with selectable audio tracks, timestamps, cancellation, progress, and no required cloud account.
- [ ] Jump from a transcript result to the matching timeline position and carry selected transcript ranges into clips or highlights.
- [ ] Add a storage dashboard grouped by game, date, protection state, source media, cache, transcript, and export usage.
- [ ] Detect duplicate recordings and repeated imports without deleting either copy automatically.
- [ ] Detect corrupted or partially readable recordings during indexing and keep the original visible with recovery guidance.
- [ ] Keep search indexes, transcripts, and derived metadata local, bounded, rebuildable, and removable independently from source recordings.

Milestone 12 is complete when a user can locate a spoken or tagged moment across a large local library, understand where storage is going, and identify duplicate or damaged media without risking automatic data loss.

### Milestone 13: Highlight Studio And Export Queue

- [ ] Build a highlight reel from clips, markers, transcript ranges, and manually selected ranges across one or more sessions.
- [ ] Add reorder, trim, preview, title-card, transition, aspect-ratio, and output-preset controls without modifying source recordings.
- [ ] Suggest optional highlights from explicit tags, automatic markers, audio peaks, scene changes, and transcript signals.
- [ ] Explain why each highlight was suggested and require user selection before it enters a reel.
- [ ] Add a persistent export queue with reorder, pause, resume, cancel, retry, progress, and contained per-job errors.
- [ ] Save reusable export presets and optionally exit Blackbox or shut down Windows after the queue completes.
- [ ] Resume safe queued work after restart while discarding incomplete temporary outputs.

Milestone 13 is complete when several moments can be reviewed, arranged, and exported as one polished video through a recoverable queue while every original recording remains untouched.

### Milestone 14: Advanced Capture Composition And Privacy

- [ ] Capture multiple selected windows in one session with configurable picture-in-picture and side-by-side layouts.
- [ ] Add reusable layout templates, per-source crop and fit, z-order, borders, and live preview.
- [ ] Keep isolated audio ownership clear when more than one application is captured.
- [ ] Add a privacy mode with application exclusions, notification suppression, sensitive-window warnings, and configurable automatic masking or source hiding.
- [ ] Show an unmistakable preview warning whenever a private or excluded source could become visible.
- [ ] Record layout and privacy decisions with the session metadata without storing sensitive window contents in diagnostics.

Milestone 14 is complete when a user can safely record a multi-window composition, verify every visible and audible source, and prove that configured private applications and notifications were excluded.

### Milestone 15: Local Companion Control

- [ ] Add an optional phone and tablet control surface for status, recording, protection, markers, instant clips, capture switching, and microphone mute.
- [ ] Keep LAN access disabled by default and separate it from the loopback-only OBS dock service.
- [ ] Use explicit pairing, short-lived credentials, revocation, rate limits, and a clearly displayed network boundary.
- [ ] Require local confirmation before enabling remote recording or microphone controls.
- [ ] Serve the companion interface from Blackbox without cloud hosting, analytics, or third-party assets.
- [ ] Provide a responsive low-bandwidth status view that remains usable while a game is fullscreen.

Milestone 15 is complete when a newly paired local device can control an approved subset of Blackbox, an unpaired device cannot discover useful controls, and revocation immediately removes access.

### Milestone 16: Plugin SDK And Game Integrations

- [ ] Define a versioned plugin manifest, capability model, event schema, compatibility policy, and developer documentation.
- [ ] Run third-party integrations out of process so a crash or hang cannot stop recording.
- [ ] Require explicit permissions for process metadata, local files, network access, markers, notifications, and capture control.
- [ ] Support game-specific event detectors that create structured markers and metadata without injecting into games.
- [ ] Add per-plugin enable, disable, update, health, log, and removal controls.
- [ ] Ship a sample integration and a deterministic test harness for recorded or synthetic game events.
- [ ] Prevent plugins from reading recordings, microphone data, credentials, or OBS capability tokens unless the user grants the exact capability.

Milestone 16 is complete when a sample game integration can produce useful timeline events, survive plugin-host failure, and be fully disabled or removed without changing core Blackbox behavior.

### Milestone 17: Updates, Rollback, And Release Safety

- [ ] Add opt-in stable and preview update channels with clear release notes and version visibility.
- [ ] Download updates through a staged process and verify signed manifests, checksums, expected filenames, and package structure before installation.
- [ ] Never install while recording, recovering, playing, transcribing, or exporting.
- [ ] Preserve the current version and local configuration until the replacement starts successfully.
- [ ] Add one-action rollback to the previous working version without rolling back recordings or databases destructively.
- [ ] Back up and migrate settings and databases atomically, with forward-compatibility checks and a no-write failure path.
- [ ] Report update, migration, startup, and rollback outcomes in Diagnostics without logging credentials or private paths.

Milestone 17 is complete when Blackbox can update and roll back across a failed-start simulation without interrupting active work, losing recordings, corrupting local state, or trusting an unverifiable package.
