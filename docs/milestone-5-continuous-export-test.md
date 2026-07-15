# Milestone 5A Continuous Session Export Test

This procedure verifies the first Milestone 5 delivery: recordings-library indexing, continuous multi-segment playback, range selection, and one-file MKV or MP4 export.

## Prerequisites

- Complete `docs/obs-test-setup.md` and confirm Blackbox reports that OBS is ready.
- Record for longer than the configured segment length so the test produces at least two completed MKV segments.
- Keep internet access available the first time the recordings library is opened. Blackbox installs its checksum-verified video tools automatically.

## Procedure

1. Stop recording so the final segment is complete.
2. Click `Recordings` in the Blackbox window.
3. On first use, wait for the video-tool download and recording index to finish.
4. Select the newest session and confirm it shows at least two segments, the expected combined duration, and `Continuous and ready`.
5. Click `Play session` and seek across a segment boundary. Confirm video and audio continue without returning to the library between files.
6. Close the player, move the `Start` and `End` controls to a short range, and click `Export selection`.
7. Save once as MKV and once as MP4. Confirm progress completes and `Open export` opens one playable file of the selected duration.
8. Click `Use full`, export as MKV, and confirm the result is one file whose duration matches the session.
9. Inspect the exported file in a player or editor and confirm it has five readable audio streams: full listening mix, game audio, voice chat, raw microphone, and processed microphone.
10. Start a long export, click `Cancel`, and confirm no partial video remains beside the chosen destination.
11. During playback or export, click `Apply Quotas` in the main window. Confirm source segments in use are not deleted.

## Failure Checks

- Temporarily move one source segment out of the recordings folder, refresh the library, and confirm Blackbox reports missing media or a timeline gap and refuses to silently export across it. Restore the file afterward.
- A failed or canceled export must leave the original segmented recordings untouched.
- The `Exports` folder must not be indexed as new source footage.

## Current Milestone Boundary

Milestone 5A does not yet include the integrated thumbnail and waveform timeline, markers, protected-range visualization, per-track mute/solo/volume controls, export presets, or separate WAV export. Those items remain in Milestone 5 and must be completed before Milestone 6 begins.
