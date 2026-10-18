# Milestone 11 Instant Clips Test

## Automated Coverage

- Clip requests, actual source ranges, statuses, output paths, failures, and
  source-segment ownership survive a SQLite restart.
- Newly closed OBS segments are indexed by the background worker before a clip
  is finalized; opening the Recordings page is not required.
- Clip output is written through the existing atomic FFmpeg export path and is
  excluded from normal session indexing.
- Failed clips retain source segments for retry. Ready clips release source
  ownership, and discard removes their output and metadata.
- Pending clip ranges and marker context are skipped by quota cleanup before
  source ownership has been fully reconciled.
- Marker context, origin, and explanation migrate safely into existing
  databases. Deleting a marker releases source ownership and marker notes.
- Session, marker, clip, and selected-range notes round-trip through SQLite.
- Automatic audio and scene detectors honor per-game enablement, thresholds,
  cooldowns, and input-rule priority while explaining each decision.
- Duration parsing accepts presets and custom values from one second through
  thirty minutes.
- WPF resource validation covers the clip inbox, notes workspace, marker
  controls, and per-game automatic-marker presets.

## Manual Acceptance

1. Start a recording and choose `30s`, `2m`, `5m`, or a custom duration in the
   Capture workspace.
2. Select **Save clip**, then continue recording until the current OBS segment
   closes.
3. Open **Recordings** and select **Clip inbox**.
4. Confirm the item moves from Pending to Ready and **Open clip** plays one
   continuous MKV.
5. Select **Open source** and confirm Blackbox opens the original session with
   the saved range selected.
6. Add, edit, and delete notes for the session, a marker, the selected range,
   and the clip.
7. Add a marker with before/after context and confirm the marker row shows its
   origin and context.
8. Protect, unprotect, mark exported, retry a failed item, and discard a test
   clip. Confirm each state remains correct after restarting Blackbox.
9. In **Games**, select an automatic-marker preset for one remembered game.
   Confirm another game left on Inherit remains disabled.
10. Record loud and visually distinct moments. Confirm generated markers state
    Audio spike or Scene change and retain an explanation.

## Remaining Milestone Work

- Register and execute the saved keyboard/controller marker rules.
- Add individual detector toggles and numeric threshold controls instead of
  preset-only tuning.
- Complete a long live soak with several OBS segment boundaries and deliberate
  encoder failure/retry cases.

## Helldivers 2 Detection Maintenance

- The running-application catalog now unions `EnumWindows` with the desktop
  Z-order chain and retries transient snapshots.
- Protected Steam processes can be recovered by PID even when the Windows
  process snapshot does not expose an executable name.
- Large visible Steam game surfaces remain eligible when fullscreen or
  anti-cheat window styles do not resemble a normal taskbar window.
- Helldivers 2 and HD2 Arsenal were detected as separate applications in 12 of
  12 consecutive refreshes of the packaged WPF Games picker.
- Release verification: 223 tests passed with 0 failures and 0 skips.
