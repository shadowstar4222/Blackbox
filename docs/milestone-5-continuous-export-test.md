# Milestone 5 Continuous Timeline And Export Test

This procedure verifies continuous multi-segment playback, the visual timeline, markers and protected ranges, track mixing, and one-file MKV or MP4 export with optional separate WAV files.

## Prerequisites

- Complete `docs/obs-test-setup.md` and confirm Blackbox reports that OBS is ready.
- Record for longer than the configured segment length so the test produces at least two completed MKV segments.
- Keep internet access available the first time the recordings library is opened. Blackbox installs its checksum-verified video tools automatically.

## Procedure

1. Stop recording so the final segment is complete.
2. Click `Recordings` in the Blackbox window.
3. On first use, wait for the video-tool download, recording index, thumbnails, and waveform to finish. Reopen the same session and confirm its timeline loads from cache.
4. Select the newest session and confirm it shows at least two segments, the expected combined duration, and `Continuous and ready`.
5. Drag the playhead across a segment boundary. Confirm the cursor time, nearest thumbnail, and waveform cursor update without changing session duration.
6. Click `Play from cursor` and confirm playback begins near the selected time and continues across segment boundaries.
7. Add a marker, confirm it appears in the marker list and timeline, remove it, and confirm it disappears.
8. Select a short range, click `Protect selection`, refresh the library, and confirm the protected range remains highlighted.
9. Open `Audio & export`. Try a preset, then change mute, solo, and volume controls and confirm the preset becomes `Custom`.
10. Select separate WAV output for raw and processed microphone tracks, choose MKV, and click `Export selection`.
11. Confirm progress completes, `Open export` opens one playable video of the selected duration, and both named WAV files appear beside it.
12. Export a short MP4 and confirm it opens in a player or editor with readable names for every included audio stream.
13. Click `Use full`, restore the full-mix preset, export as MKV, and confirm the result is one file whose duration matches the session.
14. Start a long export, click `Cancel`, and confirm no partial video or WAV files remain beside the chosen destination.
15. During playback or export, click `Apply Quotas` in the main window. Confirm source segments in use are not deleted.

## Failure Checks

- Temporarily move one source segment out of the recordings folder, refresh the library, and confirm Blackbox reports missing media or a timeline gap and refuses playback, timeline generation, and export. Restore the file afterward.
- Replace a copied test segment with an invalid media file, refresh the library, and confirm Blackbox keeps it visible as damaged instead of silently dropping it.
- A failed or canceled export must leave the original segmented recordings untouched.
- The `Exports` folder must not be indexed as new source footage.
