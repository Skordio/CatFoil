# Probes

Reflection-driven checks that build the real WinForms windows and the real
renderer out of `bin\Debug`, then assert on what comes back. They exist because
CatFoil has no test project and most of what can break here is visual: a control
laid out past the edge of its panel, a badge that renders at the wrong opacity, a
settings migration that silently drops a custom icon.

```powershell
dotnet build
pwsh -NoProfile -STA -File scripts\probes\run-all.ps1
```

Screenshots and render baselines land in `artifacts\probes\` (gitignored).

## Rules these follow, and why

**Never let `Settings.Save()` run.** `Environment.GetFolderPath(ApplicationData)`
uses the shell API and ignores `$env:APPDATA`, so a probe *cannot* be pointed at
a scratch settings file — any save overwrites the real one. Probes therefore
dispose forms rather than closing them (closing flushes), avoid pumping past the
500 ms debounce after an edit, and test the settings model by handing JSON
strings straight to `JsonSerializer` + `EnsureOverlays()`.

That rule is **enforced, not remembered**: every probe snapshots settings.json at
startup and restores it byte-for-byte in a `finally`, printing a NOTE if it had
to. This is not belt-and-braces — `probe-3d` really did destroy a live
settings.json by driving the editor UI and then pumping longer than the debounce,
which is a very easy mistake to make and leaves no trace that anything happened.
Keep the guard on any new probe that builds a `SettingsForm`.

**`-STA` and pwsh 7.** Windows PowerShell 5.1 can't load the .NET 8 assembly and
fails by handing back a null form, which looks like a pass.

**Never lock the keyboard.** `probe-hook-resync` constructs a `KeyboardHook` but
never calls `Install`, so no hook exists and nothing can swallow a key; state is
driven purely by reflection. Keep it that way — nothing here may install a hook.

**Never strand a scheduled task.** `probe-task-sddl` creates a real (disabled,
trigger-less) task as a fixture. Two Task Scheduler calls fail *after* damaging
the task, leaving one that needs elevation to remove: passing an SDDL to
`RegisterTask`, and stamping a protected DACL (`D:P`). Both are refused by
`TrySetTaskSddl` or avoided by the probe, and the fixture name carries the PID so
a stranded one can never block the next run.

## Gotchas that cost real time

- Reflection needs `.PSObject.BaseObject` on **every** reference-type argument.
  A plain cast into an `object[]` still hands reflection a `PSObject` wrapper and
  it throws. Don't name that array `$args` either — it's a PowerShell automatic.
- Screenshot with `PrintWindow` (`PW_RENDERFULLCONTENT`, flag 2), not
  `Graphics.CopyFromScreen`, which grabs whatever is in front and silently
  captures the wrong window. Build the P/Invoke with `Add-Type -MemberDefinition`;
  a `-TypeDefinition` helper touching `Graphics` fails on
  `System.Private.Windows.GdiPlus` under pwsh 7.
- `SendKeys` needs a genuinely foreground window and delivers nothing, silently,
  in a probe. Invoke `ProcessCmdKey` directly instead.
- `Register-ObjectEvent` queues handlers for the PowerShell event loop, so they
  never fire in a synchronous probe. Use
  `type.GetEvent(name).AddEventHandler($obj, [Action]{...})`, which fires inline.
- PowerShell auto-unwraps `Nullable<T>`: assert on `$item.Position.X`, not
  `.Value.X`, which yields `$null` and false-fails.

## What's here

| Probe | Covers |
| --- | --- |
| `probe-overlay-model` | Settings model, legacy migrations, corrupt-file recovery |
| `probe-render` | Badge output byte-compared against a captured baseline |
| `probe-3c` | Opacity uniformity, blocked/ring opacity, shape, colour |
| `probe-3e` | Icon gallery: ink, fit, centring, tint, cache ownership |
| `probe-multi-overlay` | Several live badges: identity, cascade, independence |
| `probe-settings` | Settings shell renders every page |
| `probe-behavior` | Immediate-apply: live announce, debounced write |
| `probe-scroll` | A page scrolls once its content outgrows the window |
| `probe-3a` | Overlay list, cards, sub-page navigation, default position |
| `probe-3d` | Overlay editor: show-in dropdown, immediate apply, layout |
| `probe-glyphs` | Contact sheet of candidate gallery glyphs (visual, manual) |
| `probe-topmost-reassert` | Badge climbs back above a later-raised topmost window |
| `probe-hook-resync` | Watchdog resync clears stale modifier/chord state (hook never installed) |
| `probe-stats-page` | Statistics page: values, live in-progress time, reset, timer lifecycle |
| `probe-task-sddl` | Logon task's security descriptor: delete grant, no write, self-repair |
| `probe-unlock-cue` | Combo/chord keys are swallowed silently; everything else still cues |
| `probe-no-chord` | Chord option gone from the UI; engine dormant, stored data kept |
| `probe-restore-ui` | Elevation relaunch re-opens the windows that were up |

`probe-render` compares against `artifacts\probes\render\*.bin`. Capture a new
baseline with `-Mode baseline` **before** a change you intend to be invisible,
then `-Mode check` after. `-Scale` is the alpha the layered window applied on top
of `Draw` when the baseline was taken: 255 since opacity moved into the renderer.

One wrinkle seen once and not explained: a seeded baseline had `countdown64`
differing in RGB while alpha matched exactly — the signature of the text being
rendered with subpixel antialiasing in one run and grayscale in the other, even
though `Draw` sets `TextRenderingHint.AntiAlias` explicitly. Re-seeding fixed it
and it has been stable since. If `countdown64` alone reports a large RGB delta
with **zero** alpha delta, suspect the baseline before suspecting the renderer.
