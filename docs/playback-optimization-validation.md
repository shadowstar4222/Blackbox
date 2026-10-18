# In-App Playback Optimization

October 13, 2026, local 2.0.0-preview.4. This is a focused playback pass, not a
claim that Blackbox is the fastest recorder across all hardware and games.

## Implemented

- Main-window review for sessions, clips, and exported videos. Microphone
  comparisons use an in-app audio control. The external player launcher and
  FFplay runtime dependency are removed.
- One lazily initialized LibVLC runtime, initialized off the UI thread.
  Hardware decoding is requested; availability and fallback depend on the GPU.
- Native video rendering without copying each playback frame into a WPF bitmap.
- Cached timeline graphics and allocation-free indexed segment lookup.
- Rapid scrubbing coalesces to the newest request every 60 ms.
- Library refresh and follow indexing run away from the UI thread, with progress
  marshalled back to the desktop controls.
- Frame navigation reuses the active decoder. LibVLC 3 hardware output can
  acknowledge NextFrame without changing pixels, so hardware frame seeking uses
  a brief muted presentation interval. Exact frame output is codec-dependent.
- Player disposal runs off the UI thread. Leaving review/minimizing releases
  the player and keeps the source cursor. Segment leases last until stop/dispose
  completes, including leases for appended segments.
- The WPF native video host/overlay is explicitly disposed on the UI thread;
  validation checks that closing review does not leave an extra app window.
- Time-shift observes finalized segment publication, starts with one segment of
  buffering, and waits safely at the current edge. It never reads active MKVs.
  The wait can be as long as the configured segment duration; it is not a
  zero-delay live preview or guaranteed gapless audiovisual transition.
- Review starts muted during capture and mutes when capture begins. Windows 10
  system-mix capture can otherwise record the app's own review audio. Unmuting
  review explicitly can still include that sound in a system-mix recording.

## Evidence

- 336 automated tests pass. A legacy OBS provisioning test hit a transient
  temporary-directory move denial on the first full run; the full rerun passed.
- Real in-app decoding was exercised at 1240x780 and 1024x680. Native HWND
  attachment, decoded/displayed-frame counters, and native frame PNGs confirmed
  nonblank output. WPF renders alone do not capture a native video surface.
- Standalone MKV and MP4 preview paths were exercised in the same page, using
  the bundled probe and shared decoding runtime without a default-player launch.
- Thirteen alternating/forward frame operations per size did not reopen the
  active segment. Pixel hashes changed on forward stepping, and subsequent
  native snapshots remained valid.
- One hundred immediate scrub requests dispatched one seek. Thirty cursor
  updates caused zero static timeline rebuilds.
- Finalized-segment append resumed playback from the waiting edge. This uses
  deterministic segment arrival in the UI harness, not a long gaming session.
- A 2,000-segment / 2,000-lookup local microbenchmark measured the original
  lookup at 515.290 ms and 144,608,176 allocated bytes; the indexed lookup took
  0.281 ms and allocated zero bytes. This is a lookup measurement, not an
  end-to-end FPS or CPU improvement claim.
- Removing FFplay saves 103,382,528 uncompressed bytes in this local package.
- After a ten-second settling interval, one five-second isolated idle sample
  measured 0.00% of one CPU core after all players closed. Automatic capture was
  disabled in that test profile; this is not a general idle-CPU guarantee.

Short startup/hidden CPU samples and working-set diagnostics are retained in
the harness output; they include setup activity and are not steady-state
performance benchmarks. Remaining work includes multi-hour recording plus
review, per-vendor GPU/encoder measurements, 1080p/1440p/4K load comparisons,
exact-frame caching, and a true low-latency preview path with bounded buffers.

## Reproduce

Use an isolated profile populated with healthy multi-segment test recordings:

```powershell
$env:BLACKBOX_DATA_DIRECTORY = 'C:\BlackboxTests\Playback'
$env:BLACKBOX_PLAYBACK_SMOKE = '1'
.\Blackbox.App.exe --ui-smoke-test
```

UI renders, native frame PNGs, and playback diagnostics go to ui-snapshots in
that profile. The UI harness never uses the normal profile for this entry point.

## Primary References

- [VideoLAN runtime lifetime and disposal guidance](https://docs.videolan.me/libvlcsharp/docs/best_practices.html)
- [VideoLAN media player API](https://docs.videolan.me/libvlcsharp/api/LibVLCSharp.Shared.MediaPlayer.html)
- [VideoLAN WPF rendering integration](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp.WPF/README.md)
