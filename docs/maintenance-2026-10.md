# Native Cleanup And Efficiency

October 13, 2026. Local 2.0.0-preview.2 maintenance build.

## Removed Or Updated

- Removed dormant OBS dock controls, handlers, service registrations, and the
  desktop's project reference. Dock source and tests remain separate for the
  explicitly deferred dock milestone.
- Removed the unused FFmpeg network installer and download URL options. Moved
  the active bundled-tool checker into the export library and tested missing
  tools, cancellation, and repeated local checks.
- Removed the unused automatic-marker background subscriber; native automatic
  highlight analysis remains deferred. Manual tags and markers remain active.
- Removed a disabled startup toggle and the obsolete microphone-page Close
  button. Updated microphone help, tray state names, startup result names,
  dependency notes, and the portable release notes.

## Runtime Work Reduced

- Precomputed microphone envelope coefficients and frame-constant gains; used
  equivalent linear-domain dynamics to avoid repeated log/exp conversions.
  Identical settings no longer reset the filter history.
- Idle workers block on the command channel. Active capture checks run at 4 Hz
  and commands wake the loop immediately.
- Health/status consumers use recent pushed worker events, with a 1.5-second
  stale-status fallback. Rejected commands no longer replace valid live status.
- The UI reuses the decoded guardian preview until its source image changes.
- Marker retention uses committed repository revisions to skip unchanged idle
  scans; active marker context is still reconciled. Repositories without change
  tracking retain the original uncached behavior.
- Session markers are grouped once by session rather than rescanned for every
  session in a large recording library.

## Reliability

Switching capture away from a minimized window now resumes the microphone along
with video. The extended live smoke test checks that packets advance after this
switch, then checks target closure, restart, and parent-disconnect finalization.

## Measurement

The repeatable microphone benchmark uses 10,000 twenty-millisecond frames
(200 seconds of audio) and the Broadcast chain, after warmup. On this machine,
the baseline took 3301.8 ms and the optimized version took 1590.2 ms, a 51.8%
reduction. Both reported the same final output peak, 0.013009426, and 40 bytes of
managed benchmark allocation. This measures the DSP loop, not gameplay FPS or
whole-system recording cost.

```powershell
dotnet run --project tests/Blackbox.Capture.Smoke -c Release -- --benchmark-microphone
```

## Compatibility Kept

Shared recorder interfaces still use some historical `Obs` names because the
coordinator, tests, and deferred dock share those contracts. Existing settings,
recording layouts, and old multitrack playback/export remain readable. The
legacy backend implementations are not registered by the native application.

Long gaming sessions, physical-device disconnect/reconnect, and hardware encoder
selection across GPU vendors remain separate validation work in the roadmap.

## Verification

- Release builds completed without warnings or errors; 326 tests passed
  (293 application/storage/export tests and 33 native/DSP tests).
- The self-contained package passed construction of the main window and every
  workspace page with an isolated data profile, and passed the worker probe.
- The packaged extended live test verified the saved headset microphone, system
  audio, live preset changes, resize, minimize/resume, switching while minimized,
  target closure, repeated starts, and parent-disconnect finalization.
- Microphone packets advanced from 206 before the minimized-window switch to 319
  after it, with zero dropped frames. Nine H.264/AAC segments were finalized,
  each with 48 kHz audio; no active segment remained.
- The package shrank from 987,276,574 to 962,232,832 bytes before adding this report,
  with 111 fewer files and no dock or ASP.NET Core assemblies. Bundled video
  tools and playback dependencies remain the largest parts of the package.
