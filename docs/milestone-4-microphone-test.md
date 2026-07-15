# Milestone 4 Manual Test

This procedure verifies microphone selection, calibration, raw/processed separation, and disconnect recovery.

## Prerequisites

- Complete `Setup OBS` in the current Blackbox build.
- Connect the microphone you plan to use.
- Stop any normal Blackbox recording before creating a comparison.

## Calibration

1. Start Blackbox and click `Setup OBS`. This repeat setup adds the input-gain filter and reloads the Advanced output profile.
2. Confirm the status changes to `OBS is installed, configured, and ready.`
3. Click `Open Recordings` and confirm Windows opens `%USERPROFILE%\Videos\Blackbox`.
4. Click `Calibrate Mic`.
5. Select the intended microphone.
6. Keep quiet and click `Measure` beside `Background noise`.
7. Speak at a normal conversational level and measure `Normal voice`.
8. Speak at the loudest volume expected during a game and measure `Loud voice`.
9. Confirm each row shows a peak and average level, and recommendations appear after the third measurement.
10. If clipping is reported, lower the microphone input level in Windows or the device software and repeat all three measurements.
11. If automatic gain control is suspected, disable it in the device software when possible and repeat the measurements.
12. Click `Apply recommendations`, close the window, reopen it, and confirm the same microphone remains selected.

## Before And After

1. Open `Calibrate Mic` and complete the three measurements if needed.
2. Click `Record comparison` and speak consistently during both five-second samples.
3. Wait until `Open before` and `Open after` appear.
4. Open both files and confirm the processed sample has less background noise and more controlled peaks without sounding clipped.
5. Confirm both comparison files appear in the recordings folder.

The comparison files contain the normal Blackbox scene. Track 1 contains the microphone path being compared; track 4 remains the untouched raw microphone and track 5 remains the processed microphone.

## Disconnect And Reconnect

1. Start a normal recording and speak for several seconds.
2. Disconnect the selected microphone for at least five seconds while recording continues.
3. Reconnect the same microphone and wait up to four seconds.
4. Speak again, then stop the recording.
5. Confirm the recording duration covers the entire test, including the disconnected interval.
6. Confirm the microphone is silent during the disconnected interval and resumes after reconnection.
7. Inspect tracks 4 and 5 in a multi-track-capable player or editor. Confirm track 4 is unprocessed and track 5 contains the calibrated filter chain.

## Expected Limitations

- Automatic gain control detection is a level-based warning, not a driver setting inspector.
- Automatic reconnection restores the saved device only when Windows reports the same device identifier.
- Blackbox does not switch to a different microphone automatically when the selected device is unavailable.

## Troubleshooting

- Logs are under `%LOCALAPPDATA%\Blackbox\logs`.
- The selected device and recommendations are stored in `%LOCALAPPDATA%\Blackbox\microphone.json`.
- Rerun `Setup OBS` if calibration reports a missing source or filter.
- Stop a normal recording before using `Record comparison`.
