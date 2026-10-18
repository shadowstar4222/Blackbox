# Blackbox Project Preferences

## Disposable Old Builds

- Always make superseded software versions easy to remove.
- Keep the current runnable build at a clearly identified, stable path. The current output path is `../../outputs/Blackbox-NATIVE-NEXT`.
- After validating and installing an update, consolidate superseded build folders, distribution ZIPs, and their checksums under `../../outputs/DELETE-OLD-BLACKBOX`. Do not scatter old versions across the output directory or create separate top-level archive folders.
- When names collide, use a version/date subfolder inside `DELETE-OLD-BLACKBOX`; never overwrite existing contents.
- Confirm an old build is no longer running or needed by the current build before moving it. A dependency's old timestamp alone does not make it obsolete.
- Keep current source code, user settings, databases, recordings, and required runtime files out of the disposable folder.
- Do not permanently delete old builds automatically. Keep the consolidated folder ready for manual deletion unless the user explicitly requests deletion.
- At handoff, identify the current build and the single folder that can be deleted.
