# Milestone 7E Capture Quality Of Life Validation

Completed on 2026-07-18.

## Delivered

- Probe-free OBS setup on Blackbox startup, enabled by default and independently toggleable.
- A clear automatic-recording-at-startup option backed by the existing remembered-application detector.
- Persisted recording resolution, FPS, and audio-bitrate settings.
- Match-application, 720p, 1080p, 1440p, and 4K resolution choices.
- 30, 60, and 120 FPS choices.
- 160, 256, and 320 kbps audio choices applied to every OBS track.
- Manual and automatic session folders organized as `Application\YYYY-MM-DD`.
- Recursive library import, media recovery, and completed-segment scanning.
- Live source reframing when a captured window changes size, without stopping the recording or hiding the OBS source.

## Automated Validation

- Release build: 0 warnings, 0 errors.
- Tests: 138 passed, 0 failed, 0 skipped.
- New coverage verifies settings defaults and persistence, quality request generation, application-name sanitization, nested scanning and recovery, organized automatic recording paths, probe-free startup setup, and resize reframing without stop/start calls.

## Live Windows Validation

Test host: Windows 10 Home 22H2, build 19045, x64.

1. Reused the running private OBS process and launched the updated Release build.
2. Confirmed startup recovery completed and automatic OBS preparation ran without starting or stopping a recording.
3. Confirmed the OBS profile persisted 1920 x 1080, 60 FPS, and 256 kbps on all six configurable track bitrate fields.
4. Opened the Settings drawer with keyboard navigation and confirmed all startup and recording-quality controls fit in the drawer.
5. Started and stopped a manual recording from the Blackbox UI.
6. Confirmed the output was written under `Videos\Blackbox\Manual\2026-07-18`.
7. Probed the MKV with FFprobe: 16.05 seconds, H.264, 1920 x 1080, 60 FPS, and five AAC tracks.
8. Confirmed Blackbox and OBS remained responsive and the setup pass created no extra recording file.

## User Check

1. Open `Settings`.
2. Leave `Set up OBS when Blackbox starts` enabled.
3. Choose a resolution, frame rate, and audio quality, then select `Apply recording quality`.
4. Enable `Automatic recording at startup` after remembering the applications Blackbox should record.
5. Resize a remembered application while recording and confirm the OBS source remains visible and fitted.
6. Open the recording folder and confirm new sessions are grouped under the application name and recording date.
