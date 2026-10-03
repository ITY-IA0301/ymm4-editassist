# Verification scope

## Automated checks

The Build.ps1 script builds the plugin against the local YMM4 4.56.1.1 installation and runs checks before packaging.
Tests use a separate process and synthetic video source/player. They do not connect to the user's open YMM4 instance or edit any project.

Build against the supplied local host: **55 checks passed; 0 compiler warnings / 0 errors**. This is not a live playback/export result.

- Scheduling: supported FPS values; original waits untouched; subtracting elapsed work/native waits; invalid timings; bounded waits; measurement-only mode.
- Bounded 60-sample metrics; separate preparation/draw costs; true redraw intervals rather than idle polls; mode transitions; reset.
- Harmony: original calls/time/usage preserved; source/edit/draw exceptions propagate; OFF passthrough; measurement-only; idle mode; owner cleanup; competing patch refusal.
- Scope: no global Task.Delay patch, no shared TimelineSource.Update patch, no preview-loop/error-recovery rewrite. Hooks only preview Edit and Draw.
- Actual host: file version and IL SHA-256 guards for loop/Edit/Draw; Edit's only caller verified as preview loop; reflection signatures; actual Edit/Draw hook compilation and installation in the isolated test process.
- WPF controls construction and offscreen rendering; default playback choice; tool unload turns optimization OFF; pending extra waits can be interrupted by OFF.

## Separation checks

PreviewLite has no reference tab, media catalog, relinking session, decoder probe, history storage or project-editing model. It does not depend on EditAssist or Newtonsoft.Json. Those features now live only in EditAssist 0.3.0.

## Not verified by automated checks

Actual video playback, GPU completion/presentation, lip sync, transitions, mouse editing comfort, real tool menu opening, and final exported pixels/audio are **not** verified.
CPU call timing is not a GPU profiler. Preparation timing includes Edit, frame/time calculations, and video source update; it is not a standalone decode measurement. A FPS cap reduces update frequency, not the cost of one decode/effect operation.
No claim of measured speedup on the user's recordings is made.

## Manual acceptance before regular use

1. Save a copy of a project; do not overwrite the original during the trial.
2. Test normal playback, clip boundaries, and transitions for 20–30 seconds each.
3. Measure the same segments with OFF/normal observation, then ON at 20fps and 15fps.
4. Check sound speed, sound/image sync, seeking, stopping, mouse editing, and device recovery.
5. Compare a short export made while OFF with one made while ON: video dimensions/FPS/frame content and audio must remain the same.
6. Confirm closing the tool restores original behavior; remove the plugin outside user/plugin and restart if any problem remains.

Internal method guard for supported host:
`YukkuriMovieMaker.Player.TimelineVideoPlayer.<BeginVideoTask>b__152_0`
IL SHA-256: `C71AFC17AB4E6B5685F7ADC2A575D664189A8A15432FECC6CB6CB64DB4CDEB35`.
