# Milestone 10 Game Profiles 2.0 Test

## Automated Coverage

- Versioned profile overrides persist through SQLite and legacy databases
  migrate to an inherited profile.
- Invalid frame rates, storage limits, crop values, shortcut conflicts, and
  unsafe recording-control bindings are rejected.
- Resolving one game's overrides does not mutate global settings or another
  game's effective profile.
- Recording startup receives the effective segment, audio-routing,
  microphone-processing, and microphone-mute choices.
- OBS transforms receive percentage crop values and preserve them during
  window-size refreshes.
- Applied profile metadata records the exact quality, timing, routing, crop,
  export, notification, and input values used for an automatic session.
- WPF static resources, the main window, and the Games profile editor are
  checked by the packaged startup smoke path.

## Manual Acceptance

1. Start two remembered games.
2. Open **Games** and select the first game.
3. Set a visible profile such as 720p, 30 FPS, 160 kbps, one-minute segments,
   a two-second delay, and 2% edge crop. Select **Apply profile**.
4. Select the second game and leave every option on **Inherit global**.
5. Return to each game and confirm the effective summary remains independent.
6. Start automatic capture for the first game and inspect OBS video settings,
   microphone state, crop, and the session's `blackbox-session.json`.
7. Stop recording, select the second game, and begin automatic capture.
8. Confirm the second game uses global quality and has no crop.
9. Resize both game windows and confirm OBS refits the selected source without
   losing the configured crop.
10. Select **Reset to global** on the first game and confirm its summary reports
    zero overrides.

## Remaining Milestone Work

- Register and execute saved keyboard/controller bindings while a profile is
  active.
- Add the final shortcut editor and live conflict feedback.
- Resolve hardware encoders safely from OBS capabilities and apply quality
  presets without assuming a GPU vendor.
- Apply per-game export defaults and notification filtering.
