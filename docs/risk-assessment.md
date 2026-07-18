# Risk Assessment

## High Risks

- OBS application audio capture behavior can vary across Windows versions and OBS releases.
- Isolated process audio plus disabled desktop audio requires careful source graph validation to avoid duplicate or missing audio.
- Device disconnect/reconnect handling can silently desynchronize tracks if silence insertion is not explicit.
- Storage pruning can destroy footage if database/file reconciliation is wrong.
- Frame-accurate export may require video re-encoding for edge segments, which can surprise users expecting stream copy.

## Medium Risks

- Global hotkeys can conflict with games or overlays.
- Game detection from foreground process and GPU activity can misclassify launchers.
- Protected or elevated games can restrict process metadata available to a normal user process.
- Long-running thumbnail/waveform generation can contend with recording IO or leave stale cache data.
- Crash recovery may preserve damaged files that require clear UI labeling and repeatable probing.
- MP4 export compatibility can conflict with multi-track workflows.
- Downloaded capture backends create supply-chain and partial-installation risks.

## Mitigations

- Keep capture, storage, database, export, and UI isolated behind interfaces.
- Prefer MKV segments and temporary filenames while active.
- Never delete files unless validated against database state.
- Add integration tests around reconciliation and quota deletion before Milestone 2 is considered complete.
- Treat raw microphone as non-destructive input and route processed microphone separately.
- Log every recording, import, export, and deletion decision with structured context.
- Keep protected footage immutable to quota pruning unless a future explicit user action unlocks it.
- Run quota deletion tests against real files so file-system and database state transitions stay paired.
- Download OBS only over HTTPS, validate its published SHA-256 digest, extract through a staging directory, and keep the private copy outside Program Files.
- Validate every OBS websocket response and make setup repeatable without recreating existing resources.
- Keep raw and processed microphone sources alive across disconnects, emit silence while unavailable, and restore both paths only when the saved device identifier returns.
- Cap calibration gain against the loud-voice peak, warn on clipping or suspicious gain control, and retain the untouched raw track for recovery.
- Download the FFmpeg toolset only over HTTPS, verify the separately published SHA-256 checksum, and install through a unique staging directory.
- Reject continuous playback and export when a session has missing media, a timeline gap, or incompatible segment formats.
- Keep playback and export source segments leased against quota deletion, and publish exports only after atomic partial-file completion.
- Limit generated thumbnails and waveform buckets, key the cache to source size and modification time, and stage new assets atomically.
- Re-probe stable media during library refresh and keep damaged rows visible with a diagnostic message.
- Stream-copy video whenever trimming is unnecessary, and transcode only audio when track mixing or volume changes require it.
- Show visible taskbar windows for explicit user selection, require an executable to be remembered before detection, and keep automatic capture opt-in.
- Confirm the same candidate across multiple polls and wait through a stop grace period before ending an automatic recording.
- Use limited-information process queries as a fallback without requesting administrator privileges.
- Read bounded Toolhelp ancestry only for visible candidate windows and require two consecutive handoff matches before persisting a child executable as an alias.
- Treat GPU counters as optional corroboration and continue with remembered executable and foreground-window signals if PDH is unavailable.
- Keep learned aliases visible and removable, and never treat an unremembered executable as a game without an explicit alias or verified launcher ancestry.
- Probe only stable media during startup and skip files that are recent or still changing.
- Stage recovery remuxes beside the source, verify the staged file, atomically replace only after success, and preserve the original in a dedicated backup folder.
- Adopt a surviving OBS recording after restart instead of issuing a duplicate recording command or interrupting the active output.
- Expose categorized current-session logs and indexed-media health in the diagnostics window so recovery and capture failures remain inspectable.
