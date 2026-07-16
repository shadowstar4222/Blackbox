# Milestone 6A Automatic Capture Test

This procedure verifies non-invasive foreground Steam-game detection, OBS source binding, automatic recording ownership, and delayed stop behavior.

## Prerequisites

- Complete `docs/obs-test-setup.md` and confirm Blackbox reports that OBS is ready.
- Have a Steam game installed locally. A game in a secondary Steam library is supported.
- Stop any current manual recording before the automatic-start test.

## Automatic Start

1. Start Blackbox and click `Check OBS` or `Setup OBS` until setup succeeds.
2. Click `Enable Auto`. Confirm the automatic-capture line says it is watching for a foreground Steam game.
3. Start a Steam game and bring its main window to the foreground.
4. Within about five seconds, confirm Blackbox shows the game title and changes from confirming to recording.
5. Open the private OBS window and confirm `Blackbox Game Capture` and `Blackbox Game Audio` both target the game's window.
6. Play for longer than one segment boundary and confirm independent MKV segments continue to appear in the recording folder.

## Focus And Stop Behavior

1. Briefly Alt-Tab away from the game and return within 15 seconds. Confirm recording does not stop.
2. Exit the game and keep it closed. Confirm Blackbox displays the stop countdown and stops recording after about 15 seconds.
3. Open `Recordings` and confirm the completed footage is indexed and playable.

## Manual Ownership

1. Disable automatic capture and click `Start Recording`.
2. Enable automatic capture, then focus the Steam game until it is detected.
3. Click `Disable Auto`. Confirm the manually started recording continues.
4. Click `Stop` to end the manual recording.

## Failure Checks

- Foreground desktop apps such as File Explorer, browsers, Discord, Spotify, OBS, and Blackbox itself must not trigger recording.
- A detection or OBS binding failure must be shown in the automatic-capture status and logged without terminating Blackbox.
- Automatic capture must not request administrator privileges or inject into Steam or the game.
- Selecting `Stop` while automatic capture owns the recording must disable automatic capture before stopping OBS.

## Current Boundary

Milestone 6A recognizes foreground Steam games from Steam library paths or Steam process ancestry. GPU corroboration, manually configured executable profiles, launcher handoff, crash recovery, and the diagnostics workspace remain in Milestones 6B and 6C.
