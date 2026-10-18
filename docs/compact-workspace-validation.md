# Compact Workspace Validation

The October 17 preview.11 status-consolidation pass is documented in
UI-CLEANUP-20261017.txt, including responsive rendering and review navigation.

October 13, 2026. Initial workspace pass: 2.0.0-preview.3. Rechecked with the
2.0.0-preview.4 playback pass; see playback-optimization-validation.md for its
additional decoding and lifecycle evidence.

## Changes

- Reduced the navigation rail from 216 to 176 pixels and the header from 72 to
  52 pixels. Default window size is 1240x780; minimum is 1024x680.
- Replaced stacked dashboard cards and duplicate navigation with an unframed
  preview, a compact health/audio inspector, and expandable session details.
- Kept recording, clips, protection, and manual moments in a persistent strip.
- Added a searchable command palette with availability checks, click/Enter
  execution, arrow navigation, Escape dismissal, and Ctrl+K access.
- Added application/date/marker search, Today/Protected/Needs attention filters,
  and newest/oldest/longest ordering in the recording library.
- Tightened shared controls, tabs, lists, and scrollbars. Split startup settings
  and recording quality into two columns; kept review actions outside the
  timeline's scrollable area.
- Fixed the editable clip-duration template so its selected text is visible.
- Games lists now scroll by pixels rather than whole items, matching the profile
  settings pane while retaining recycling virtualization. The alias list uses
  the same pixel-scrolling behavior.

## Verification

- Release app builds have zero warnings and errors. The original workspace pass
  had 326 passing automated tests; the playback pass has 336.
- Isolated UI validation rendered Capture, Recordings, Games, Microphone,
  Diagnostics, Settings, and command search at both supported test sizes.
- Populated validation used three synthetic recording sessions and nine real
  H.264/AAC test segments. Local timeline thumbnail/waveform generation succeeded.
- UI checks covered command search and keyboard focus, the default clip duration, search clearing,
  matching applications, media-health filtering, and duration ordering.
- A running packaged-app check verified Ctrl+K access, command filtering,
  Enter execution to the Microphone page, and Escape back to Capture.
- Games scrolling checks used 200 synthetic rows per list at both window sizes,
  verified 13-pixel offsets in all lists and the settings pane, and checked that
  distant rows were not realized. Test data was isolated from the normal profile.
- Rendered pages were visually inspected; the first pass exposed and corrected
  a blank duration control, narrow settings, an empty timeline state, and
  horizontal scrolling in the application picker.

The explicit UI validation entry point requires BLACKBOX_DATA_DIRECTORY so it
cannot run against the normal user profile accidentally:

```powershell
$env:BLACKBOX_DATA_DIRECTORY = 'C:\BlackboxTests\CompactWorkspace'
.\Blackbox.App.exe --ui-smoke-test
```

PNG renders and result.txt are written to that profile's ui-snapshots folder.
Planned transcription, highlight editing, isolated audio mixing, and composition
features are documented in product-direction.md; they are not enabled by this UI
release.
