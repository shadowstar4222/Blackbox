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
- Long-running thumbnail/waveform generation can contend with recording IO.
- Crash recovery may preserve damaged files that need clear UI labeling.
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
