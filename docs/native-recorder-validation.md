# Native Recorder Validation

Validation history for the native recorder, updated October 17, 2026.
Native Blackbox 1.0.0 promotes the functionality validated through preview.18.
The previous OBS-based release is preserved as v1.0.0-obs-legacy. Older counts
below record the tests at each earlier stopping point, not the current total.

## Native 1.0.0

The current suite passes 378 main tests and 54 native-capture tests (432 total).
The latest controls use distinct single-frame, filmstrip, timed-skip, and
recording-boundary pictograms. Packaged checks verify glyphs, accessible action
names, fixed hit targets, nonblank toolbar captures, and compact-window fit.
Existing playback, pause-stable frame stepping, decoder reuse, scrolling, and
24 timeline resize/content cases remain covered. See [current release details](RELEASE-1.0.0-NATIVE.txt)
and [control validation](PLAYBACK-ICONS-20261017.txt). Version promotion does not
remove the documented hardware/game and long-duration validation limits.

## Historical Preview.17 Publication

At preview.17 publication, 369 main tests and 54 native-capture tests passed (423 total).
Packaged UI checks cover all pages at 1240x780 and 1024x680, including embedded
playback, frame stepping, and virtualized diagnostics scrolling. Timeline layout
checks add 24 content/resize combinations up to 1920x1080, with 0, 3, and 200
markers, preview/no-preview states, bounded scrolling, and action visibility.

Preview.12 restored positive capture/audio feedback and compacted the microphone
and diagnostics panels. Preview.13 repaired automatic-capture controls;
preview.14 carried automatic recording into the diagnostic setup window.
Preview.15 fixed diagnostic pixel scrolling, preview.16 themed the remaining
controls, and preview.17 removed the expanding empty marker panel.
See [release details](RELEASE-2.0.0-preview.17.txt) and the corresponding dated
validation notes for scope and limitations. These checks do not replace
long-duration gameplay testing across hardware vendors.

## UI Status Consolidation

Preview 11 keeps recording/watching/readiness in the left sidebar, removes
duplicate routine labels and the second review back button, and preserves
warnings, failures and measured capture details. Packaged checks passed at
1240x780 and 1024x680, including ready/watching/recording states, warning visibility,
review back navigation and embedded playback controls. 352 main tests and
43 native tests passed. See `UI-CLEANUP-20261017.txt` for scope and evidence.

## Recording Setup Window

Preview 10 replaces preview 9's playback demo with a capturable setup window.
The original background video is retained and loops indefinitely, accompanied
by richer original music. Applied recording settings are captured in its overlay.
An isolated, microphone-disabled recording test passed: 23.765 seconds of H.264
1264x720 and stereo 48 kHz AAC, with recorded music (-29.3 dB mean / -11.6 dB peak)
and no black interval detected in the unobscured scene crop across the loop.
Resizing, Record/Stop commands, temporary target release, automatic-capture
restoration and unchanged saved preferences passed. Main tests: 350 passed;
native tests: 43 passed. See `RECORDING-SETUP-20261017.txt` for scope and reproduction.

## Segment Transition Flash Fix

Preview 8 replaces per-file handoffs for compatible completed MKV sessions with
a seekable virtual Matroska stream. One decoder/output spans segment boundaries;
no full recording is copied or re-encoded. Tested older recordings, synthetic
media, and the five-minute sync fixture: no decoder reopen, output loss, or
native-background paint at measured joins. The recorder-clock fixture measured
-1.5 ms of start-to-end drift across 154 matched pulses. A 118-segment, 9.91 GiB
session passed short playback and repeated open/close checks. All 382 automated
tests passed, including 29 focused virtual-stream cases. See
`SEAMLESS-PLAYBACK-20261017.txt` for detailed evidence and scope limits.

Preview 7 paints the native VLC host black while its video child is absent.
The default native STATIC surface reproduced a 240/255 light-gray background;
the fixed surface measured 0/255. Tests on an older VRChat session and recent
variable-frame-rate captures exercised automatic transitions, loop restart,
finalized-segment following, paused cross-segment seeks, and frame navigation.
All 30 handoff background samples were black; source SHA-256 hashes were unchanged.
The fix does not copy video frames, re-encode media, or change the recorder.
See `PLAYBACK-TRANSITIONS-20261017.txt` for evidence and limitations.

## Audio/Video Clock Fix

Preview 6 disables forced constant-framerate scheduling so Media Foundation keeps
capture-clock video timestamps. The requested capture rate, hardware encoder,
bounded compressed transport, and video stream-copy are retained. Nominal-FPS
metadata is separate from timestamps and keeps review/export metadata stable.
Five-minute pulse analysis measured 3.5 ms of start-to-end drift across 154
matched pulses; the joined MKV measured -22.5 ms. Extended lifecycle, in-app
playback, frame controls, and real MKV/MP4 exports passed. All 353 automated tests
passed, including six nominal-FPS cases and an enabled real-media integration test.
See `AUDIO-VIDEO-SYNC-20261016.txt` for reproduction and limitations.

## Architecture

The WPF app uses NativeRecordingController and a JSON-lines child-process client.
Blackbox.Capture.Worker embeds ScreenRecorderLib 7.0.1, Windows Graphics Capture,
Media Foundation H.264/AAC encoding, and WASAPI audio. FFmpeg only copies encoded
packets into finalized MKV segments; it does not recapture or re-encode video.

Compressed bytes cross a bounded 4 MiB queue. Initialization metadata is bounded
to 1 MiB. Media Foundation requires position queries and final header duration
backpatches, even for fragmented MP4. A header shadow accepts those final patches
without corrupting the forward-only muxer stream; rewrites of media are rejected.
This fixes a reproduced zero-output/finalization deadlock from the initial draft.

The worker checks native frame progress AND muxed output before acknowledging
start. HWND/PID validation, pause-on-minimize, closed-target finalization, bounded
native calls, stdin lifetime tracking, and a child-process job guard prevent
silent capture of another window and orphan FFmpeg processes.

The microphone is captured separately through WASAPI, downmixed and resampled to
48 kHz mono, processed in reusable 20 ms buffers, and written to FFmpeg through a
bounded named pipe. FFmpeg stream-copies video while mixing the processed voice
into stereo AAC. The chain provides high-pass filtering, SpeexDSP suppression,
expansion, voice EQ, compression, de-essing, and limiting.

## Tests Run

- Release app and worker builds: zero warnings/errors.
- Automated solution tests: 287 main tests plus 23 native tests (310 total),
  all passed. Includes stream metadata patching, PCM peak/RMS, sizing, detection,
  microphone-disabled start, worker-failure state, sampled guardian checks,
  game-switch session ownership, normal window closure, and incremental finalization.
- Packaged startup construction smoke and native prerequisite probe passed.
- Real Windows 10 build 19045 capture of an animated WPF test window passed.
- 720p60 video test: five playable H.264 MKVs, four 5-second segments and one
  2.617-second tail. Resized and restored frames were decoded and inspected.
- System-audio test: stereo AAC at 48 kHz, audible test-tone signal measured at
  -35.1 dB mean / -30.6 dB peak. Video remained 60 FPS.
- Live target switch, closed-target finalization, repeated start after stop and
  parent stdin disconnect passed the extended smoke harness.
- The packaged app's Games picker, Remember, Use for capture, manual Start, and
  Stop controls were exercised through keyboard UI automation in an isolated
  profile. The 51.178-second output contained 1264x680 H.264 at 60 FPS and 48 kHz
  stereo AAC; native logs confirmed selected-microphone and system-mix capture.
  The guardian returned a nonblack 320x172 preview. This did not prove microphone
  speech level or physical-device reconnection behavior.
- Worker CPU in the synthetic tests: 8.3% of ONE core without audio and 14.8% of
  ONE core with system audio. Not whole-machine percentages; excludes FFmpeg and
  the fixture. This is not a gameplay benchmark or a before/after OBS comparison.
- Final 2.0.0-preview.1 package: startup smoke exited 0; the extended live test
  passed with normal target-close status and clean parent-disconnect shutdown.
  All nine output MKVs passed full FFmpeg decode with error-on-corruption enabled;
  no active segment remained. The final system-audio run used 13.2% of ONE core
  for the worker during its approximately 22-second measured interval.
- October 13 microphone DSP pass: 291 application tests plus 31 native tests
  passed (322 total), with zero Release build warnings or errors. A real saved
  USB headset microphone produced 1,293 frames and 2,034,459 bytes across five
  playable H.264/AAC MKV segments. Each segment contained stereo AAC at 48 kHz.
  Microphone packets were verified before start completed, minimized-window
  pause/resume and finalization passed, no pooled audio frames were dropped, and
  the optimized worker used 11.1% of one CPU core in the synthetic test.

Reproduce the live test on an unlocked Windows desktop:

```powershell
dotnet run --project tests\Blackbox.Capture.Smoke -c Release -- <worker.exe> <ffmpeg.exe> <test-output-folder> --extended
```

The fixture uses its own window and an optional quiet synthetic audio tone.
The extended test writes only to the supplied output folder and shuts down its
worker. Windows 10 system-loopback testing can also include other audible apps.

## Remaining Gates

The October 13 maintenance build additionally passed 326 tests, packaged startup
construction, and the extended microphone/system-audio live test. All nine MKVs
contained H.264 and 48 kHz AAC with no active files left behind. A minimized-target
switch resumed microphone packets with zero drops. See
`maintenance-2026-10.md` for the cleanup changes and DSP benchmark.

- Live Helldivers 2 and sustained gaming tests at 1080p60/1440p/120 FPS.
- NVIDIA/AMD/Intel encoder selection reporting and software-fallback policies.
- Multi-hour memory/disk stability, GPU device loss, sleep/resume, low disk, and
  Windows shutdown tests on physical machines.
- Real microphone unplug/replug and default-device-switch matrix.
- Default output-device switching during a recording; native capture force-reset.
- Native separate audio tracks and automatic audio/scene highlights.
- Public package licensing/source-compliance review, signing, and release notes.

The prior Blackbox-LATEST package remains untouched. Preview limitations are
documented in START-HERE.txt and unsupported UI controls are disabled.

Windows UI screenshot automation was unavailable on this host (the automation
helper's capture-border interface returned E_NOINTERFACE). Keyboard actions and
accessibility states were used for the app test; encoded frames were inspected
separately. A legacy OBS installer unit test hit one transient directory-move
access error during a full run; the full suite passed on rerun. One build attempt
hit a running test-fixture file lock; closing the fixture restored a clean build.

## References

- [ScreenRecorderLib source](https://github.com/sskodje/ScreenRecorderLib)
- [Media Foundation fragmented MP4 sink requirements](https://learn.microsoft.com/en-us/windows/win32/api/mfidl/nf-mfidl-mfcreatefmpeg4mediasink)
- [Windows taskbar window rules](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar#managing-taskbar-buttons)
