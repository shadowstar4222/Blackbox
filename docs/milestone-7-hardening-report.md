# Milestone 7 Hardening Report

Completed on 2026-07-18.

## Audit Scope

- Reviewed 229 tracked files, including 215 source and test files across the App, Domain, Export, Infrastructure, Recording, Storage, and Tests projects.
- Established clean Release build and test baselines before changing behavior.
- Ran the exhaustive .NET analyzer set and prioritized correctness, security, native loading, resource ownership, SQL construction, impossible branches, and asynchronous API use.
- Exercised startup recovery, one-click OBS setup, automatic game detection, capture binding, recording, stop grace, media probing, and WPF shutdown on Windows 10 build 19045.

## Reliability And Security Changes

- Settings and OBS configuration now use atomic temporary-file publication. Failed writes cannot replace the last good in-memory or on-disk state.
- Automatic capture and microphone monitoring own and cancel their background loops deterministically instead of inheriting short-lived caller cancellation.
- App and window shutdown now await service cleanup; recording, playback leases, hotkeys, semaphores, websocket sessions, and portable provisioners release their resources predictably.
- Native library loading is restricted to the Windows system directory for platform DLL imports.
- OBS websocket reads reject non-text and oversized messages, and failed handshakes dispose their socket.
- The portable OBS websocket password is no longer exposed on the process command line.
- Portable OBS installation is serialized and uses contained staging cleanup.
- One-click setup retries OBS status code 207 while a newly launched backend is still initializing, but continues to fail immediately for other request errors.
- SQLite uses shared-cache connections, WAL journaling, a busy timeout, pooled connections, and asynchronous protected-range transactions.
- Migration SQL is selected only from compile-time allowlists.

## Performance And Boundaries

- Healthy unchanged recordings reuse persisted metadata; a no-change library refresh does not provision FFmpeg or probe media again.
- Timeline generation is serialized per cache and checks for a valid cache hit before provisioning FFmpeg.
- Timeline cache data is limited to 1 GB and 30 days by default, with expired and excess signature directories pruned.
- Diagnostic logs are limited to seven 2 MB files by default. The reader seeks into file tails and keeps only its bounded result queue instead of loading complete logs.
- OBS websocket messages are limited to 4 MB.
- Support bundles cap recent diagnostic events and publish through an atomic partial ZIP.

## Support Bundle Privacy

Open `Diagnostics`, click `Support bundle`, and read the disclosure before approving a destination.

The ZIP includes:

- Blackbox, Windows, and .NET version details.
- Recording-state counters, indexed-media health, storage size, and recovery summary.
- A capped sample of recent categorized diagnostic messages.
- A plain-text privacy manifest.

The ZIP excludes:

- Video and audio recordings.
- Screenshots.
- The Blackbox database.
- OBS passwords and settings.
- Microphone configuration and device identifiers.
- Saved game profiles and executable lists.
- Application settings files.

Included event text is redacted for credentials, user-profile paths, URI credentials, and microphone device identifiers. The bundle remains local until the user chooses to share it.

## Automated Validation

- Release build: 0 warnings, 0 errors with warnings treated as errors.
- Tests: 114 passed, 0 failed, 0 skipped.
- Reliability repetition: five final full-suite runs passed consecutively after earlier repeated passes.
- Coverage: 72.97% line and 53.88% branch overall.
- Storage stress: 40 concurrent segment/profile/marker writers.
- Lease stress: 32 tasks performing 250 duplicate-lease iterations each.
- Failure injection covers atomic settings write failure, support-bundle cancellation and cleanup, oversized diagnostics, expired timeline cache, duplicate timeline generation, background-service cancellation, recording recovery timing, and OBS status-207 readiness retries.
- Exhaustive analyzer pass: zero production findings in the selected high-priority CA1001, CA1508, CA1849, CA2000, CA2100, CA2213, CA5392 categories.

The exhaustive analyzer also reports advisory API-design, logging-performance, globalization, and `ConfigureAwait` style rules. These are tracked separately because applying all of them would create broad framework-style churn without changing Blackbox behavior.

## Live Windows 10 Validation

Test host: Windows 10 Home 22H2, build 19045, x64.

1. Started Blackbox with its private OBS runtime stopped.
2. Ran one-click OBS setup from a cold state.
3. Confirmed Blackbox waited through OBS initialization, applied the profile/collection/scene, made a two-second probe recording, and reached `OBS is installed, configured, and ready.`
4. Launched StarVester from Steam app ID 4194800.
5. Confirmed Blackbox matched the remembered `Starvester.exe`, its live `YYGameMakerYY` window, and a 1920 x 1080 client area.
6. Confirmed detection sources included foreground window, Steam process tree, Steam library, configured executable, and GPU activity.
7. Confirmed OBS game capture reported successful D3D11 shared-texture capture and Blackbox entered `Recording Starvester`.
8. Closed StarVester and confirmed recording stopped after the configured 15-second grace period.

Resulting media:

- File: `2026-07-18 16-59-41.mkv`
- Size: 74,842,608 bytes
- Duration: 95.433 seconds
- Video: H.264, 2880 x 1620, 30 fps
- Audio: five AAC streams
- Visual checks: gameplay and UI were visible during the running-game interval; the final post-exit grace interval was black as expected.

## Remaining Platform Risk

- The live pass covers the minimum supported Windows generation on one NVIDIA/OBS/game combination. Different GPU vendors, protected games, anti-cheat systems, HDR modes, and unusual multi-monitor scaling can still require targeted compatibility testing.
- Hardware interruption behavior is guarded and covered by service tests, but every physical microphone and USB driver cannot be reproduced automatically.
- Long recordings remain segmented on disk by design; continuous playback and one-file export hide those safety boundaries from normal use.
