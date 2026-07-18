# Milestone 5 Advanced Player Test

1. Record long enough to create at least three Blackbox segments, then stop cleanly.
2. Open `Recordings`, select the finished session, move the cursor away from zero, and click `Play from cursor`.
3. Confirm the embedded Blackbox player opens at that position and the numbered `S1`, `S2`, and `S3` bands match the physical segment boundaries.
4. Exercise play/pause, start/end, both 10-second jumps, previous/next segment, both frame-step controls, speed, loop, volume, mute, audio-track selection, and fullscreen.
5. Drag across every segment boundary and confirm playback continues without opening another window or resetting the global time.
6. Add each quick tag, add a custom event label, and confirm gold pins appear at the current playback positions.
7. Double-click a marker, use previous/next marker, remove one marker, and confirm the recording-library timeline updates immediately.
8. Close the player and confirm the session can still be exported or pruned normally after its temporary media lease is released.

Automated and smoke verification on 2026-07-16:

- 104 automated tests passed.
- A real 30 fps MKV exposed six audio choices and accepted decode, seek, pause, rate, and audio-track operations through LibVLC.
- A real three-segment WPF player opened from a non-zero offset, played, rendered at 1280 x 800 and 960 x 620, and closed cleanly.

Playback performance regression verification on 2026-07-18:

- The Release build completed with zero warnings or errors and all 122 automated tests passed.
- A real 2880 x 1620, 30 fps MKV advanced and reversed by exactly one 33 ms frame per request without sticking.
- Forward and reverse requests share one serialized queue. Blackbox advances the logical playhead without touching the decoder for every queued click, then reopens one decoder at the final requested frame.
- A live 2880 x 1620, 30 fps recording remained visible after a 24-frame burst followed by 10 spaced frame requests; decoder memory returned after the superseded native resources were released.
- Selecting a recording loaded only an existing timeline cache. A cache miss no longer launches FFmpeg until `Build preview` is requested.
- Repeated `Play from cursor` requests reused one player window instead of stacking full-resolution LibVLC decoders.
- Closing the player released its native decoder and reduced the measured Blackbox working set from about 500 MB to 228 MB.
