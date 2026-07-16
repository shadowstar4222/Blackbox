# Milestone 6 Automatic Capture Test

This procedure verifies remembered-game detection, start-time OBS source setup, automatic recording ownership, and delayed stop behavior.

## Prerequisites

- Complete `docs/obs-test-setup.md` and confirm Blackbox reports that OBS is ready.
- Have a game installed and stop any current manual recording.

## Remember A Game

1. Start the game and leave its main window running.
2. In Blackbox, click `Games` and then `Refresh`.
3. Select the game under `Running applications` and click `Remember selected game`.
4. Confirm it appears under `Remembered games` with automatic recording checked.
5. Close and reopen Blackbox, then confirm the remembered entry is still present.

## Automatic Start

1. Click `Check OBS` or `Setup OBS` until setup succeeds.
2. Click `Enable Auto`. Confirm the status says it is watching for a remembered game.
3. Start the remembered game if it is not already running.
4. Within about five seconds, confirm Blackbox changes from confirming to binding and then recording.
5. In private OBS, confirm the `Blackbox Recording` preview shows the game at the correct aspect ratio.
6. Confirm `Blackbox Game Capture` and `Blackbox Game Audio` both target the live game window.
7. Play for longer than one segment boundary and confirm MKV segments continue to appear.

## Selection And Stop Behavior

1. Run an unremembered game or desktop application and confirm it does not trigger recording.
2. Briefly Alt-Tab away from the remembered game and confirm recording continues.
3. Exit the game and keep it closed. Confirm Blackbox shows the stop countdown and stops after about 15 seconds.
4. Open `Recordings` and confirm the completed footage is indexed and playable.

## Manual Ownership

1. Disable automatic capture and click `Start Recording`.
2. Enable automatic capture while a remembered game is running.
3. Confirm Blackbox asks for the manual recording to stop instead of reconfiguring or stopping it.
4. Click `Disable Auto`, confirm the manual recording continues, then click `Stop`.

## Profile Controls

1. Clear the checkbox beside a remembered game and confirm it no longer triggers automatic recording.
2. Check it again and confirm detection resumes.
3. Use `Remove`, confirm the prompt, and verify that game no longer triggers recording.

## Failure Checks

- A detection or OBS binding failure must be shown in the status and logged without terminating Blackbox.
- Automatic capture must not request administrator privileges or inject into the game.
- Selecting `Stop` while automatic capture owns the recording must disable automatic capture before stopping OBS.

## Current Boundary

Milestone 6B remembers exact executable paths selected from active windows. Executable aliases, launcher handoff, GPU corroboration, crash recovery, and the diagnostics workspace remain planned.
