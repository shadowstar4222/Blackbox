# Milestone 6 Automatic Capture Test

This procedure verifies remembered profiles, per-game preferences, aliases, launcher handoff, GPU corroboration, OBS rebinding, recording ownership, and delayed stop behavior.

## Prerequisites

- Complete `docs/obs-test-setup.md` and confirm Blackbox reports that OBS is ready.
- Have a game installed and stop any current manual recording.

## Remember A Game

1. Start the game and leave its main window running.
2. In Blackbox, open `Games` and click `Refresh`.
3. Confirm the running row shows its executable, live client size, and GPU percentage or `GPU unavailable`.
4. Select the game and click `Remember as game`.
5. Select the remembered profile and confirm its settings show automatic recording, game audio, launcher handoff, and GPU preference.
6. Close and reopen Blackbox, then confirm the profile and settings remain present.

## Profile Preferences

1. Clear `Capture game audio`, enable automatic capture, and start the game.
2. Confirm OBS captures video while `Blackbox Game Audio` is muted and disabled.
3. Stop automatic capture, restore `Capture game audio`, and enable `Prefer GPU-active window`.
4. Start automatic capture again and confirm Diagnostics or the log reports `GpuActivity` when the game is using at least 1% GPU.
5. If the GPU counter is unavailable, confirm executable detection still starts recording normally.

## Executable Aliases

1. Run the remembered game and a second executable that belongs to the same game.
2. Select the remembered profile, select the second running application, and click `Add as alias`.
3. Confirm the full alias path appears under `Executable aliases`.
4. Close the primary executable and confirm the alias can trigger the same profile.
5. Select the alias and click `Remove alias`, then confirm it no longer triggers the profile by itself.

## Launcher Handoff

1. Enable `Follow launcher handoffs` for a profile whose primary path is a launcher.
2. Start the launcher and let it open the final game process.
3. Confirm Blackbox sees the same child executable in two consecutive scans before adding it as an alias.
4. Confirm automatic capture rebinds OBS to the final game window and preserves the profile display name and audio preference.
5. Reopen `Games` and confirm the learned child path is visible and removable.
6. Repeat with launcher handoff disabled and confirm no child alias is learned.

## Automatic Start And Stop

1. Click `Enable Auto` and start an enabled remembered game.
2. Within about five seconds, confirm Blackbox changes from confirming to binding and then recording.
3. In private OBS, confirm the preview shows the game at the correct aspect ratio.
4. Confirm `Blackbox Game Capture` and, when enabled, `Blackbox Game Audio` target the live game window.
5. Briefly Alt-Tab away and confirm recording continues.
6. Exit the game and keep it closed. Confirm Blackbox stops after the 15-second grace period.
7. Open `Recordings` and confirm the completed footage is indexed and playable.

## Manual Ownership

1. Disable automatic capture and click `Start Recording`.
2. Enable automatic capture while a remembered game is running.
3. Confirm Blackbox does not reconfigure or stop the manual recording.
4. Disable automatic capture, confirm the manual recording continues, then click `Stop`.

## Recovery And Failure Checks

- Follow `docs/milestone-6c-recovery-diagnostics-test.md` for crash recovery and surviving OBS-session adoption.
- An OBS binding, GPU-counter, or detection failure must be logged without terminating Blackbox.
- Automatic capture must not request administrator privileges or inject into the game.
- Selecting `Stop` while automatic capture owns the recording must disable automatic capture before stopping OBS.
- An unremembered application without verified launcher ancestry must never trigger recording.
