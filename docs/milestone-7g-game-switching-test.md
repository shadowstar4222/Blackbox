# Milestone 7G Active Game Switching Validation

Date: 2026-07-21  
Host: Windows 10 Home 22H2, build 19045, x64

## User Workflow

1. Keep both remembered games open.
2. Open `Games`, then `Manage remembered games`.
3. Select the running game that should appear in OBS.
4. Select `Use for capture`.
5. Confirm its row says `Active OBS capture` and the heading names the active game.

The action works while recording is active or stopped. During an active recording, Blackbox updates the OBS game-video and isolated game-audio inputs in place without stopping the recording. While stopped, it prepares the OBS preview for the selected window. If automatic capture is enabled, normal launch confirmation can still start recording afterward.

The preferred choice persists across Blackbox restarts and outranks foreground-window and GPU-activity heuristics while that game is open. If it closes, Blackbox falls back to another eligible remembered game. Selecting a profile whose per-game automatic recording is paused requires automatic capture to be turned off first.

## Automated Validation

- Release build: zero warnings and zero errors.
- Full Release test suite: 150 passed, zero failed, zero skipped.
- Selection persistence is covered through save, reload, replacement, and clear behavior.
- Detector coverage verifies preferred-game priority and fallback when the preferred game is absent.
- Controller coverage verifies idle OBS preparation and in-place switching during a manual recording.
- State-machine coverage verifies that selecting an idle target does not block automatic recording startup.
