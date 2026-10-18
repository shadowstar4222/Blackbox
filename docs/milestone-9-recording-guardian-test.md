# Milestone 9 Recording Guardian Test

## Scope

This development build adds the first Recording Guardian implementation. It:

- shows a scaled live preview of `Blackbox Game Capture`;
- reports the selected window, OBS output resolution, frame rate, recording-output state, and game-audio level;
- checks the exact OBS capture mode, window identifier, and title-match priority;
- confirms sustained black, frozen, silent, missing-preview, missing-source, and OBS-unavailable conditions;
- refreshes the exact selected capture and refits its scene item without stopping recording;
- restarts an OBS recording output that stopped while Blackbox still owns the session;
- limits repair attempts to two, with a cooldown between attempts;
- records evidence, active profile, action, and result in Diagnostics and the normal logs;
- sends notification-area messages for recording, capture, microphone, storage, repair, recovery, and fault events.

## Automated Verification

Run:

```powershell
dotnet test tests\Blackbox.Tests\Blackbox.Tests.csproj
dotnet build Blackbox.sln -c Release
```

The packaged executable also supports a noninteractive startup check:

```powershell
.\Blackbox.App.exe --startup-smoke-test
```

It initializes the real application host and constructs the main window, then exits before starting OBS setup, automatic capture, or recording.

The guardian tests inject:

- intermittent and sustained black frames;
- short and sustained static frames;
- expected and unexpected audio silence;
- wrong-window policy and inactive-source states;
- a missing OBS source;
- an unexpectedly stopped recording output;
- repeated websocket failures and a later OBS reconnect;
- resize and refit behavior without restarting the recording;
- retry exhaustion and escalation.

## Safe Live Test

Do not replace a Blackbox folder while its executable is running. Use the separate Milestone 9 package.

1. Finish or stop any important recording.
2. Exit every running Blackbox instance from its notification-area menu.
3. Extract and run the Milestone 9 package.
4. Let startup OBS setup finish.
5. Open a remembered game or application.
6. Select it under **Games**, then use **Use for capture** or enable automatic capture.
7. Return to **Capture**.
8. Confirm the Recording Guardian panel shows the intended window and a moving preview.
9. Confirm the displayed resolution and FPS match the selected quality.
10. Play audio and confirm the game-audio meter moves.
11. Start a short recording and press **Check now**.
12. Open **Diagnostics** and confirm guardian events include profile and evidence text.

## Recovery Checks

These checks are optional and should be done only with disposable test footage.

1. In OBS, hide `Blackbox Game Capture`.
2. Wait for the guardian to report the inactive source.
3. Confirm Blackbox restores the source and the recording remains active.
4. Change the game window size.
5. Confirm the preview refits without stopping the output.
6. Temporarily close OBS during a disposable recording.
7. Confirm Blackbox reports loss of OBS, then returns to healthy after OBS reconnects.

## Remaining Milestone 9 Work

- Add a Settings control for conservative, balanced, and sensitive confirmation profiles.
- Soak test long loading screens, static menus, paused games, and intentionally quiet scenes.
- Tune defaults from those live results before marking Milestone 9 complete.
