# Blackbox Product Direction

The goal is to make a recording useful without making recording itself heavier.
Capture keeps a small, predictable resource budget; indexing and creative work
can wait until the game closes or run only when requested.

## Available In This Workspace

- Compact navigation, an unframed capture preview, and an always-visible recording
  strip with recording, clips, protection, and manual moments.
- Searchable quick actions from the header or Ctrl+K. Commands reflect whether
  the action is currently available.
- Recording search across application names, dates, and marker labels; Today,
  Protected, and Needs attention filters; newest, oldest, and longest ordering.
- Existing continuous review, frame navigation, clips, tags, notes, and export.
- Sessions, clips, and exports now review in the main window using one lazy
  decode runtime. Leaving review releases the decoder while retaining the cursor.
- Time-shift follows finalized segments during capture with one segment of
  buffering. This is delayed review, not a zero-delay preview stream.

## Next: Find The Moment (Milestone 12)

Search a large library by what happened rather than remembering a filename.
Add saved views, a calendar, tags with colors, and favorites first. Then add
opt-in local transcription with timestamped results that jump directly to the
timeline. Notes, marker labels, game names, and spoken text should share one
search interface. Show what matched and preserve the original recording.

Useful workflow: search a remembered game and a spoken phrase, preview the exact
moment, mark a range, and queue a clip without joining files manually.

## Then: Shape And Deliver (Milestone 13)

Build a highlight reel from moments across sessions. Support reorder, precise
trim, waveform preview, and landscape/portrait presets. A persistent export
queue needs cancellation, retry, progress, and safe restart before batch export
or automatic recommendations grow. Keep suggestions explainable and optional.

Useful workflow: select five marked moments from a week of sessions and export
one reel while retaining the source ranges and editable project.

## Parallel: Reversible Audio (Native Audio Work)

Record game, selected applications, and microphone to isolated tracks with a
clean raw microphone safety track. Let the user adjust voice processing and mix
after recording, audition before/after, and save per-game presets. Show input
activity and a short recording test before a long session. Exclude unwanted
applications without silently replacing the selected source.

## Later: Composition And Trusted Automation (Milestones 14-16)

Add multiple-window layouts, picture-in-picture, crop/fit, and privacy exclusions
with an accurate source preview. Add optional game integrations for structured
events, and paired phone controls after the capture/control contracts are
stable. The deferred OBS dock remains separate from the native desktop shell.

## Performance And Reliability Gates

- Measure CPU, GPU, memory, encoder choice, dropped frames, and disk growth under
  actual game load; show an understandable session health summary.
- Keep transcription, waveform generation, and highlight analysis out of the
  recording path. Offer idle-only background work and cancellation.
- Make a visible decision before changing recording quality automatically.
- Exercise long sessions, resize/minimize/switch, device loss, low storage, and
  interrupted finalization before calling a capability release-ready.

Feature breadth alone is not the measure. A useful Blackbox should preserve the
moment reliably, find it quickly, and turn it into a finished artifact with few
steps.
