# Milestone 8 OBS Dock Validation

Milestone 8 adds an OBS custom browser dock to the development source. It is not included in the public v1.0.0 package.

## Implemented Scope

- Compact recording state, elapsed time, current game, and Blackbox status.
- Manual start/stop and automatic-capture toggle.
- Protect previous five minutes and add a live timeline marker.
- Open the recording timeline and microphone controls in Blackbox.
- Automatic and one-click registration in Blackbox's managed portable OBS only.
- Local preview from Settings.

## Security Boundary

- Kestrel listens on `127.0.0.1` only.
- A persistent random 256-bit token is part of every valid dock path.
- Host validation rejects alternate host headers before routing.
- Mutating requests require JSON and `X-Blackbox-Dock: 1`; no CORS origin is allowed.
- Request bodies are capped at 1 KiB and commands are serialized.
- Assets are embedded in the application and use no remote scripts, fonts, images, or analytics.
- Responses use no-store caching, no-referrer, MIME sniffing protection, a restrictive Content Security Policy, and a browser Permissions Policy.
- Registration structurally merges `ExtraBrowserDocks`, replaces only Blackbox's fixed UUID, creates a backup, writes atomically, and leaves malformed JSON untouched.

## Automated Checks

- Dock token and selected port persist across settings-store instances.
- Correct token succeeds; incorrect token and forged host fail with `404`.
- Mutation without the private command header is rejected.
- Valid dock commands reach the shared control target.
- Security headers are present and CORS permission is absent.
- Existing OBS docks survive merge and repeated registration is idempotent.
- Malformed OBS dock JSON produces no configuration or backup write.
- Portable OBS provisioning invokes managed configuration contributors.
- Pending live markers resolve to the correct session and timeline offset.
- Unmatched pending markers expire only after the seven-day retention window.
- SQLite persists marker placement updates.

Final automated verification on Windows 10 x64:

- Release build: zero warnings and zero errors.
- Automated suite: 162 passed, zero failed, zero skipped.
- Self-contained `win-x64` publish: succeeded with `Blackbox.Dock.dll` included.
- Responsive render checks: 250 px portrait and 520 px landscape produced no horizontal overflow; the live 250 px state had no clipped action labels.

Verification commands:

```powershell
dotnet restore Blackbox.sln
dotnet build Blackbox.sln -c Release --no-restore
dotnet test Blackbox.sln -c Release --no-build
dotnet publish src\Blackbox.App\Blackbox.App.csproj -c Release -r win-x64 --self-contained true -o artifacts\Blackbox-dock-test
```

## Manual OBS Acceptance

1. Start the development build and wait for Blackbox OBS setup to finish.
2. Open Settings and select **Install / repair** under **OBS Dock**.
3. Reopen Blackbox's private OBS if it was already running.
4. Confirm **Blackbox Controls** appears as a custom dock and does not appear in a personal OBS installation.
5. Resize the dock from narrow portrait to wide landscape and confirm no horizontal scrolling or clipped controls.
6. Exercise start, stop, automatic capture, protect, marker, timeline, and microphone actions.
7. Stop recording, refresh Recordings, and confirm the live marker appears at the expected timeline position.
8. Exit Blackbox and confirm the dock becomes unavailable without exposing any cached state.
