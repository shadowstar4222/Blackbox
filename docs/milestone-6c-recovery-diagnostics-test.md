# Milestone 6C Recovery And Diagnostics Test

Use this procedure to verify startup recovery without risking an existing recording library. Normal startup probing and OBS-session adoption are automatic.

## Normal Startup

1. Start Blackbox and wait for the startup progress bar to finish.
2. Confirm the status says the recovery check completed and OBS is ready.
3. Open `Diagnostics`.
4. Confirm `Indexed media` reports the expected segment count and no unexpected damaged or missing files.
5. Confirm `Last recovery` reports repaired, damaged, reconciled, and backup counts.

## Surviving Recording

1. Start a manual recording in Blackbox and wait several seconds.
2. Simulate an application failure by ending only `Blackbox.App.exe`. Do not close OBS.
3. Confirm the current MKV in `%USERPROFILE%\Videos\Blackbox` continues growing.
4. Start Blackbox again.
5. Confirm the status says `Recovered the active OBS recording.` and the `Stop` button is available.
6. Open `Diagnostics` and confirm startup reports one active file skipped plus an adopted OBS recording.
7. Click `Stop` and confirm the app returns to `Idle`.

## Damaged-File Recovery

Perform this test only with a disposable copy of a recording.

1. Close Blackbox and OBS.
2. Copy an MKV into the Blackbox recording folder and truncate the copy so FFprobe cannot read it.
3. Start Blackbox and wait for recovery to finish.
4. If FFmpeg can recover the copy, confirm the repaired file is readable and the original appears under `Blackbox Recovery Backups`.
5. If FFmpeg cannot recover it, confirm the original remains unchanged and the recordings library labels it as damaged.
6. Open `Diagnostics`, select the `Recovery` category, and confirm the attempted repair and result are visible.

## Interrupted Automatic Capture

1. Remember a running game and enable automatic capture.
2. Wait for Blackbox to begin recording that game.
3. End only `Blackbox.App.exe`, leaving the game and OBS running.
4. Restart Blackbox and confirm automatic capture returns to `On` and adopts the existing recording.
5. Close the game and confirm recording stops after the normal grace period.

## Expected Safety Behavior

- A file that is new or still changing is skipped as active.
- A failed repair never overwrites the source file.
- A successful repair is probed before it replaces the source.
- The pre-repair source is retained in the recovery-backups folder.
- SQLite is refreshed after recovery so missing, imported, and damaged files remain visible.
- Startup continues to the normal controls even when an individual file cannot be recovered.
