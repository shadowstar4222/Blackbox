# OBS Test Setup

Use this when you are ready to test Blackbox against OBS.

## OBS Requirements

- Windows 10 version 2004/build 19041 or later.
- OBS Studio 28 or later for built-in Application Audio Capture on Windows.
- OBS Studio with obs-websocket 5.x enabled. Current OBS builds expose this under `Tools > WebSocket Server Settings`; the default websocket port is `4455`.

## Recommended OBS Profile

1. Open OBS.
2. Create a dedicated profile named `Blackbox`.
3. Create a dedicated scene collection named `Blackbox`.
4. In `Settings > Output > Recording`, choose:
   - Recording format: `mkv`
   - Encoder: your preferred hardware encoder, such as NVENC, AMF, Quick Sync, or x264
   - Audio tracks: enable tracks `1` through `5`
5. In `Settings > Audio`, disable global desktop audio when testing isolated application sources.

## Sources To Add Manually For Testing

1. Add a Game Capture or Window Capture source for your game.
2. Add `Application Audio Capture (BETA)` for the game process.
3. Add `Application Audio Capture (BETA)` for Discord or your voice-chat app.
4. Add your microphone once as the raw microphone source.
5. Duplicate the microphone as a separate processed microphone source.
6. On the processed microphone source, add filters in this order:
   - Noise suppression
   - Expander
   - Compressor
   - Limiter

## Track Assignments

- Track 1: full listening mix
- Track 2: game audio
- Track 3: voice chat
- Track 4: raw microphone
- Track 5: processed microphone

Keep the raw microphone source free of destructive gating. Apply filters only to the processed microphone path.

## Blackbox App Testing

1. Build with `dotnet build`.
2. Run with `dotnet run --project src\Blackbox.App\Blackbox.App.csproj`.
3. Click `Setup OBS` to validate the default websocket connection and apply the Blackbox OBS setup plan.
4. Click `Apply Audio` to validate the Blackbox audio profile and send it through the current OBS controller boundary.
5. Click `Start Recording` to exercise the Blackbox start pipeline.
6. Use `Protect 5 Min` or `Ctrl+Shift+F7` to mark recent footage in the database.
7. Use `Apply Quotas` to test pruning behavior.

`ObsWebSocketController` now sends real OBS websocket requests. If `Setup OBS` fails, check that OBS is running, websocket is enabled, the port is `4455`, and the OBS source kinds used by your installed OBS build match Blackbox's setup plan.
