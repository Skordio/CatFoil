# CatFoil — Architecture & Feature Reference

CatFoil is a Windows tray utility that **locks the keyboard while leaving the mouse
working** — so a cat walking across the desk can't type. It ships as a portable or
installed single-file WinForms app on .NET 8 (`net8.0-windows`), with no external NuGet
dependencies. Settings live in `%APPDATA%\CatFoil\settings.json`.

This document maps every window, menu, and feature, and how the pieces fit
together. It is a reference for understanding the app end-to-end.

---

## 1. Process model & lifecycle

| Concern | Where | How |
| --- | --- | --- |
| Entry point | `src/Program.cs` | `[STAThread] Main`. Sets `HighDpiMode.PerMonitorV2`, visual styles, then runs `TrayAppContext`. |
| Single instance | `src/Program.cs` | Named mutex `Local\CatFoil-SingleInstance`. A second launch sets the `Local\CatFoil-ShowMainWindow` auto-reset event and exits; the first instance is waiting on that event and responds by showing its main window. |
| App shell | `src/TrayAppContext.cs` | An `ApplicationContext` (no main form owns the lifetime). Owns the tray icon, keyboard hook, hotkey, overlay, timers, and the lazily-created windows. |
| Shutdown | `TrayAppContext.ExitApp` | Unlocks if locked, detaches watchdog/SystemEvents, hides tray, disposes hook/hotkey, closes overlay and main form, `ExitThread()`. |

The app is **tray-first**: closing the main window hides it to the tray (unless
"Hide to tray on close" is off); the process keeps running until Exit is chosen.

---

## 2. Screens & windows

### 2.1 Main window — `src/MainForm.cs`
The central lock/unlock surface. Two visual states:

- **Unlocked** (420×260): large green "Keyboard is unlocked." status.
- **Locked** (760×480, re-centered): calm gray message —
  *"The keyboard is currently locked."* —
  and the toggle button reads **Unlock Keyboard**.

Persistent controls:
- **Lock/Unlock Keyboard** button (docked bottom, large) — raises `ToggleRequested`.
- **Settings** button (top-left) — raises `SettingsRequested`. (The red Exit
  button that sat beside it was removed 2026-08-02 — quitting is a tray-menu
  action, not an everyday one.)
- **Hotkey badge** (bottom-left, above the lock button) — a custom-drawn control
  (`HotkeyBadge`) rendering the active hotkey as 3D keycaps joined by "+". Hidden
  when the hotkey is disabled.

Behaviors:
- **Close-to-tray**: `FormClosing` cancels a user close and hides, unless
  `AllowClose` is set (real exit) or the tray-on-close setting is off.
- **Timed-lock countdown**: `ShowLockCountdown` appends "Auto-unlock in m:ss"
  while a user-chosen "Lock for N minutes" runs.

### 2.2 Settings window — `src/SettingsForm.cs` + `src/Pages/`
A resizable window (900×720, min 840×560), lazily created and reused by the tray.
`SettingsForm` itself is only a **shell**: an owner-drawn navigation list on the
left and one page at a time on the right. Each page is a `SettingsPage`
(`src/Pages/`), so adding a setting is a row on a page rather than a re-layout of
one fixed-pixel dialog. Pages are parented on first visit, which is what defers
Advanced's `schtasks.exe` query until the user goes there.

`SettingsPage` (`src/Pages/SettingsPage.cs`) is the base: a single auto-sizing
column inside an `AutoScroll` panel, with `AddSection` / `AddCheck` / `AddHint` /
`AddRow` / `AddStretchRow` / `ClearRows` helpers and shared static fonts (a
settings window can be reopened repeatedly, and WinForms never disposes a Font
assigned to a control).

**Sub-pages.** Besides the seven nav entries the shell can show one page reached
from *within* another — `ShowSubPage` / `PopSubPage`, one deep. The header row
grows a back button and a breadcrumb ("Overlays › Cat badge") while the nav list
keeps highlighting the parent. The back button is an owner-drawn dark rounded
square with a white triangle (`BackButton`), not a text glyph: at header size a
"‹" was both easy to miss and clipped by its own bounds, since a glyph gives no
control over how much of the em box it fills. Escape goes back rather than closing; changing nav
selection discards the sub-page; and clicking the *already-selected* nav row pops
too, since that raises no `SelectedIndexChanged` and would otherwise strand the
user. A sub-page is owned by the shell and disposed when it goes away — it is
never in `_pages`, so `Dispose` handles it separately.

Pages:
- **General** — Hide to tray on close · Start CatFoil when Windows starts ·
  Start hidden in the system tray.
- **Locking** — hotkey enable checkbox · a click-to-capture hotkey box
  ("press keys"). Then auto-lock: enable checkbox + minutes selector. (The
  multi-key chord option was pulled 2026-08-02 — offering a hotkey whose
  leading keys leak into the focused app felt wrong. The hook's chord engine
  and the stored chord settings remain, dormant, for a possible return.)
- **Overlays** — Show overlays while locked (the master switch over every
  configured overlay), then one **`OverlayCard`** per overlay (§5.1) and an
  **Add overlay** button. Each card shows a live thumbnail painted through the
  real `OverlayRenderer`, the name, a summary line, an **Enabled** toggle, an
  **Edit** button and a **⋯** overflow menu holding Duplicate and Remove.
  **Remove is disabled at one overlay** — the model guarantees a non-empty list,
  so deleting the last one would immediately reappear as a fresh default badge.
  Duplicate copies the original's image files to the copy's own id, and leaves
  its `Position` null so it cascades rather than landing on top. The page is
  rebuilt wholesale on each change, deferred via `BeginInvoke` because the
  actions are raised from a card that the rebuild disposes.
- **Sounds** — three **independent** cues (locking · unlocking · a key blocked
  while locked), each with its own source, volume and **Test** button. A cue is
  either one of the user's Windows scheme sounds (tooltip points at the Windows
  sound settings if those events are "(None)") or a file of their own (§5.4).
  Volume applies only to a custom file — a scheme sound plays at whatever the
  system mixer says — so it greys out when the Windows sound is selected. The
  blocked cue is still throttled so a held key can't machine-gun it.
- **Statistics** — lifetime lock sessions, total locked time (ticking live
  during a lock via an in-progress-seconds callback from the tray), and
  blocked-key count, with a confirm-then-save **Reset…**. Replaced the separate
  `StatsForm` dialog; the tray's "Statistics…" entry now opens the settings
  window on this page (`SelectPage<T>`). Its 1 s refresh timer runs only while
  the page is on screen — the page is constructed hidden so the shell's first
  `Visible = true` is a real transition that starts it.
- **Advanced** — **Run as administrator (also block elevated windows)** with an
  indented sub-option **Start automatically at logon, elevated (no prompt)**.
  Checking "Run as administrator" relaunches CatFoil elevated (UAC prompt) so its
  hook can also block elevated foreground windows; if already elevated the box is
  checked and disabled. The elevated-logon sub-option (enabled only while
  elevated) creates a Task Scheduler task. See §7.
- **About** — version, and the **Welcome tour…** button.

**Immediate-apply — there is no Save/Cancel.** Pages mutate through
`SettingsSession` (`src/SettingsSession.cs`), which owns the live `Settings`
object: `Apply()` makes the edit, raises `Changed` at once (the form re-raises it
as `SettingsSaved`, so the tray applies it live), and schedules the disk write on
a 500 ms debounce so dragging a spinner doesn't write settings.json per tick.
`Flush()` forces the pending write — called on form close and before an elevated
relaunch hands off. Closing the window also sweeps unreferenced overlay images
and cue audio (`IconStore`/`SoundStore.CollectGarbage`, §5.3/§5.4). That is
deferred to here rather than done when an item is removed, so removing one and
changing your mind inside the same visit doesn't cost you the file.

While the hotkey box has focus the form raises `HotkeyCaptureChanged(true)`; the
tray suspends the current hotkey until it goes false, so the combo being rebound
lands in the box instead of toggling the lock (`ApplyHotkeySettings` guards on
`_hotkeyCapture`, which also covers the 60 s watchdog). Hotkey display strings
live in `src/HotkeyText.cs`, shared with the main window, welcome tour and tray
balloons.

### 2.3 Overlay editor — `src/Pages/OverlayEditorPage.cs`
A **sub-page** (§2.2) of the settings shell, opened from an overlay card's
**Edit** button. Immediate-apply like every other page — there is no OK/Cancel.

At the top: the overlay's **Name**, and a **Show** dropdown saying when the badge
appears — *Except in fullscreen apps* (the default) · *Only in fullscreen apps* ·
*Always*. It writes `OverlayItem.ShowIn` and nothing below it depends on the
value, so it deliberately does **not** rebuild the body.

The body: an icon source (Default cat / **Built-in icon**
with a glyph picker and colour / Custom image + **Choose…**) · sliders
for size (32–256 px), opacity, an optional different opacity while blocking, and
the blocked ring · background shape and colour (`ColorDialog`, in the box) · and
a **checkerboard live preview** (`PreviewBox`) painting through the shared
`OverlayRenderer` at true 1:1 size. A **Preview a blocked key** toggle animates
the blocked state — without it, the two settings that only apply while blocking
would have nothing to preview. The body sizes itself to what was laid out rather
than to a constant, so adding a row can't quietly clip the last one.

Each edit replaces the appearance object instead of mutating it, because the
overlay window's repaint check compares it by reference (§2.5). The body is
still rebuilt outright — rather than rebound — when the icon colour changes,
since every gallery swatch is drawn in the chosen colour and has to be redrawn.

### 2.4 Welcome window — `src/WelcomeForm.cs`
Shown once on first launch (flag `Settings.WelcomeShown`), and re-openable from
Settings → About → "Welcome tour…". A scrolling tour (auto-sized to content, since the
hotkey string is variable): what CatFoil does, Locking, Unlocking, the cat badge,
and the tray icon. Single **Get started** button.

### 2.5 Locked overlay badge — `src/OverlayForm.cs`
A small, borderless, always-on-top **layered window** (WS_EX_LAYERED +
`UpdateLayeredWindow` pushing a 32bpp ARGB bitmap) shown while locked. It never
steals focus (WS_EX_NOACTIVATE, `ShowWithoutActivation`).

**There is one of these per `OverlayItem`** (§5.1), not one per app. Each form
carries the `OverlayId` it renders, and `TrayAppContext.SyncOverlays()` reconciles
the live forms against the configured items — closing forms whose item is gone,
creating forms for new items, and pushing appearance to the rest. It is the single
path by which an overlay edit reaches the screen. A badge is active only while
*locked* **and** the master `ShowOverlay` switch is on **and** that item's
`Enabled` is true (`ApplyOverlayActivation`). An item with no saved position is
placed **three quarters across the screen and three quarters up it** — clear of
the middle, but not tucked so far into a corner it goes unnoticed — **stepped
down 88 px per item** so a newly added badge never spawns on top of an existing
one. Resolving which image to show is shared with the settings preview and the
overlay list through `src/OverlayIcon.cs`.

Features:
- **When it shows**: a 1-second poll compares `ForegroundIsFullscreen()` against the
  item's `ShowIn` and shows or hides accordingly. The badge has one appearance, so
  this decides visibility only. Re-compositing the layered window is skipped unless
  the resolved icon, size, countdown, or flash actually changed since the last paint,
  so the poll is nearly free while nothing moves.
- **Stays topmost**: each poll tick also re-asserts `HWND_TOPMOST`
  (`SWP_NOMOVE|NOSIZE|NOACTIVATE`). Topmost is a band, not a place — an app that
  raises itself topmost *later* (e.g. MPC-HC entering fullscreen) lands above the
  badge and would stay there, hiding the locked signal and defeating the
  blocked-key failsafe, which counts the buried badge as still `Visible`.
- **Draggable** (position saved, clamped to the virtual screen); a **click**
  (no drag) opens the main window.
- **Countdown text** during a timed lock (GDI+ `DrawString` so glyphs carry
  alpha on the layered surface).
- **Red flash** on a blocked keypress (`FlashBlockedKey`).
- **Manual tooltip** shown on hover (auto tooltips don't work on never-activated
  windows).
Painting is shared with the settings preview through `src/OverlayRenderer.cs`.

**Opacity lives in the renderer, not the window.** `Draw` paints the badge opaque
into its own layer and then fades that layer once through an `ImageAttributes`
`ColorMatrix`; `UpdateLayeredWindow` composites at `SourceConstantAlpha = 255`.
Two reasons. Fading each element as it is painted would make the places they
overlap more opaque than the edges — two layers at 20% read as 36%, three as 49%
— so a translucent badge would come out as a solid icon in a ghostly frame. And
the settings preview is an ordinary control with no layered window, so a constant
alpha applied by the compositor could never be previewed. `OpacityAlpha(92)` is
exactly 235, the value the window used to apply, so the default is unchanged.

**The blocked-key ring is painted outside that fade**, at its own `RingOpacity`.
`Draw` takes both `flashOn` (the 120 ms blink phase) and `blocked` (true for the
whole ~480 ms window, which is what the separate blocked opacity follows) — so
the badge holds its reaction steady while the ring pulses, and dimming the badge
to react to a keypress never dims the thing announcing it. `OverlayForm` derives
both once in `RenderIfChanged` and passes them down, and tracks `_paintedBlocked`
separately from `_paintedFlash`: the window opens with the ring in its *off*
phase, so without it the first paint would see nothing changed and the blocked
opacity wouldn't appear until the first blink.

---

## 3. System tray icon & menu

Owned by `TrayAppContext`. The `NotifyIcon` uses the app icon; its tooltip text
tracks state ("CatFoil — keyboard active" / "— KEYBOARD LOCKED").

- **Double-click** the tray icon → open the main window.
- **Right-click** → context menu:
  1. **Open CatFoil** (bold default) → show main window
  2. **Lock Keyboard** / **Unlock Keyboard** (label toggles with state)
  3. **Lock for…** submenu — 5/15/30/60 minutes, then auto-unlock
  4. **Statistics…** → the settings window, opened on its Statistics page
  5. **Settings…** → open the settings window
  6. — separator —
  7. **Exit** → shut the app down

---

## 4. Locking engine

| Piece | File | Role |
| --- | --- | --- |
| Low-level hook | `src/KeyboardHook.cs` | `WH_KEYBOARD_LL`. While **locked**, swallows every key-**down** (returns 1); key-**ups** always pass through so Windows' modifier state never desyncs. Mouse is untouched (no mouse hook). |
| Unlock while locked | `src/KeyboardHook.cs` | RegisterHotKey can't fire while keys are swallowed, so the unlock combo is detected **inside** the hook, using modifier state the hook **tracks itself** (`TrackModifier`) — never `GetAsyncKeyState` to detect a *held* key, which is blind to swallowed key-downs. Keys on the way *into* the combo or chord (`PartOfUnlockGesture`: the combo's required modifiers, the chord's keys/modifiers) are swallowed **without** raising `BlockedKeyPress` — nobody hits Alt+G in one instant, and the Alt half of the gesture must not beep, flash, or count as a blocked key. |
| Stale-state resync | `src/KeyboardHook.cs` | The hook misses key-**ups** whenever input goes where it can't see (secure desktop: Ctrl+Alt+Del / UAC / Win+L, or a silently-dropped hook's dead window), and one stale "down" modifier jams the unlock combo while blocking keeps working. `ResyncModifiers` (called by the watchdog) clears tracked keys `GetAsyncKeyState` reports **released** — safe in that one direction, and only after a 5 s event-quiet period so a genuinely held (swallowed, hence OS-"up") key, which auto-repeats through the hook, is never touched. |
| Classic hotkey | `src/HotkeyManager.cs` | `RegisterHotKey` (with `MOD_NOREPEAT`) on a `NativeWindow`; fires only while unlocked. This is the sole lock trigger in classic mode. |
| Chord hotkey (dormant) | `src/KeyboardHook.cs` | "Alt + C + F"-style chord detection (`CompletesChord`) still lives in the hook, probe-covered — but **nothing arms it**: the UI option was pulled 2026-08-02 because the chord's leading keys leak to the focused app while unlocked. The chord settings survive in settings.json as unread legacy so a re-add restores the user's chord. |
| Toggle plumbing | `src/TrayAppContext.cs` | `ToggleLock` (400 ms debounce, since lock and unlock use the same keys) → `SetLocked`. Sets hook lock state and updates UI/tray/overlay. |
| Idle resilience | `src/TrayAppContext.cs` | A 60 s **watchdog** plus power-resume / session-unlock handlers re-arm the hotkey and reinstall the hook — in both lock states — because Windows silently drops both after long idle or sleep. `Reinstall` adds the new hook before releasing the old, so the swap has no instant where a keystroke could slip past a lock. |

`Ctrl + Alt + Del` cannot be blocked (Windows reserves it) and is documented as
the always-available escape hatch.

---

## 5. Settings model — `src/Settings.cs`

JSON at `%APPDATA%\CatFoil\settings.json` (`Keys` serialized as string flags).
Notable fields: `Hotkey` (default **Alt+G**), `HotkeyEnabled` (the chord trio
`UseChordHotkey`/`ChordModifiers`/`ChordKeys` is unread legacy — option pulled
2026-08-02, data kept for a possible return), `MinimizeToTrayOnClose`,
`StartWithWindows`, `StartElevatedOnBoot`, `StartMinimized`, `ShowOverlay`,
`WelcomeShown`, `Overlays` (§5.1), plus `AutoLockEnabled`/`AutoLockMinutes` and the
lifetime `Stat*` counters. Corrupt files fall back to defaults. `Save()` writes to a
temp file and renames, so an interrupted save leaves the old or the new file — never
a truncated one.

### 5.1 Overlays — `Settings.Overlays`

A **list of `OverlayItem`**, so several badges can be on screen at once. Each item
has a stable `Id` (a GUID, which also names its image file on disk and must never
be reused after a delete), a `Name`, an `Enabled` switch, an optional `Position`,
a `ShowIn` (`ExceptFullscreen` / `OnlyFullscreen` / `Always`) saying when the badge
appears, and one `OverlayAppearance` saying how it looks:
`IconSource` (Default / Gallery / Custom) + `GalleryIconId` +
`IconColor` + `CustomIconFile` · Size 32–256 · `Shape`
(RoundedSquare / Square / Circle / None) · `BackgroundColor` (`#RRGGBB`) ·
`Opacity` · `BlockedOpacityEnabled` + `BlockedOpacity` · `RingOpacity`.
`ShowOverlay` remains a single master switch over all of them.

**When** and **how** are deliberately separate. Up to 0.4.0 they were one setting —
two complete appearances, `Normal` and `Fullscreen`, each with its own `Visible` —
which cost a duplicate of every control above to express a look over fullscreen
apps that nobody wanted.

Everything is range-checked on read (`ClampedSize`, `ClampedOpacity`,
`ClampedRingOpacity`) because settings.json is text a user can edit. Colours are
stored as hex because `Color` has no settable properties and cannot round-trip
through System.Text.Json; `HexColor.Parse` falls back to the built-in colour on
anything unreadable, including the colour *name* case, where `FromHtml` returns
transparent black instead of throwing and would otherwise yield an invisible
badge with no error.

Enums use `LenientEnumConverter`, which falls back to the default instead of
throwing. This matters more than it looks: the stock converter throws on an
unrecognised value, and `Settings.Load` catches everything and starts from
defaults — so one mistyped word, or a settings.json written by a newer CatFoil,
would silently discard the hotkey, the autostart choice and the lifetime
statistics, which cannot be recovered.

**Legacy migration.** 0.3 and earlier stored exactly one overlay, as the loose
`OverlayPosition` / `OverlayNormal` / `OverlayFullscreen` properties. Those are
still deserialized, but they are **write-suppressed** (`JsonIgnoreCondition.WhenWritingNull`)
and `EnsureOverlays()` folds them into a single `OverlayItem` on load, then nulls
them — so upgrading keeps a user's custom icon, size and position instead of
resetting to a default badge. The same code path produces the default overlay on a
fresh install, where all three are simply absent. `EnsureOverlays()` is idempotent
and guarantees a non-empty list, so any caller needing "the overlays" can just call
it. (One-way: a settings.json written by 0.4 has no overlay for 0.3 to read.)

**Collapsing the two states.** 0.4.0 and earlier gave each item a `Normal` and a
`Fullscreen` appearance. Both are still read — write-suppressed the same way — and
`CollapseStates()` folds them into one `Appearance` plus a `ShowIn`. The 0.3 fold
above feeds *into* those same legacy slots rather than straight into `Appearance`,
so one routine handles both upgrade paths. The rules are chosen to preserve what
was actually on screen:

| Was visible | → `ShowIn` | → `Appearance` |
|---|---|---|
| both | `Always` | `Normal` |
| normal only | `ExceptFullscreen` | `Normal` |
| fullscreen only | `OnlyFullscreen` | **`Fullscreen`** |
| neither | `ExceptFullscreen`, and `Enabled` cleared | `Normal` |

The fullscreen-only row takes the *fullscreen* look because that is the one the
user was looking at; taking `Normal` would silently restyle a badge whose normal
appearance had never been on screen. The last row can't be said with `ShowIn` at
all, so it moves to `Enabled` — which, unlike a dropped setting, is visible in the
list and one click to undo. Any image the discarded state owned stops being
referenced and is swept by `IconStore.CollectGarbage` (§5.3).

Two per-state properties were likewise superseded: `ShowBackground` by `Shape`,
and `UseCustomIcon` by `IconSource`. Two things about those migrations are
load-bearing. They run in their own loop **after** the legacy fold and the
collapse, not in the per-item loop before them — on a 0.3 file the states exist
only as `OverlayNormal`/`OverlayFullscreen` until that fold, so migrating earlier
would skip exactly the users the legacy properties protect. And it is
keyed strictly on `ShowBackground.HasValue`, never on `Shape` "looking like a
default": `EnsureOverlays` is called from several places, so deriving from the
current value would turn a deliberately chosen `None` back into a box. The
collapse is keyed the same way, on a legacy block being present rather than on
`ShowIn` looking default, for exactly the same reason.

### 5.2 Built-in icons — `src/IconGallery.cs`

The gallery is drawn from a Windows symbol font rather than shipped as artwork —
the repo has exactly one image and it is a placeholder. Glyphs are single-colour
outlines, so the user's `IconColor` tints them for free.

Four things about it are load-bearing:

- **`GraphicsPath.AddString` + `FillPath`, not `DrawString`.** The path gives the
  glyph's real *ink* rectangle to fit and centre on. Symbol fonts carry
  asymmetric ascent/descent, so centring by the line box sits every glyph high —
  and by a different amount each, which a row of icons shows up immediately. The
  fill colour *is* the tint, so there is no recolouring pass to get wrong, and
  `FillPath` obeys `SmoothingMode`, sidestepping text-rendering hints entirely.
- **The font is probed with `new FontFamily(name)` inside a try/catch.**
  `new Font(name, …)` does *not* throw for a missing family — it silently
  substitutes and renders tofu. Falls back Fluent → MDL2 → the bundled cat.
- **Every codepoint is one that exists in Segoe MDL2 Assets** (the Windows 10
  font), so it is also in Windows 11's Fluent set. GDI+ has no glyph-exists test
  and a family-level fallback cannot help when the family is present but the
  glyph is not, so a Fluent-only codepoint would be an empty box on Windows 10
  with nothing to catch it.
- **Renders are cached by (id, colour, size) and handed out as copies.**
  `OverlayForm.ReplaceIcon` disposes the bitmap it was given, so returning the
  cached instance would destroy it under every other consumer.

### 5.3 Custom overlay images — `src/IconStore.cs`

A chosen image is **copied** into `%APPDATA%\CatFoil\icons\{overlayId}-{token}.{ext}`
so the badge survives the original being moved or deleted; settings store only the
path **relative to** `Settings.Directory`, which keeps the portable EXE portable.
Every import lands under a **fresh** name. Reusing
one fixed name per overlay would mean choosing a different picture leaves the
stored path unchanged, so nothing downstream could tell the image needs
re-reading — and `OverlayForm.ApplyAppearance` deliberately reloads only when
that path changes, since it runs on every settings edit and would otherwise read
a file and decode a bitmap per tick of a slider. The superseded file stops being
referenced and is swept at window close.

`Duplicate()` copies an existing image to a new overlay's name, so a duplicated
overlay owns its picture rather than sharing the original's file.
`CollectGarbage()` — run when the settings window closes — deletes stored images
nothing refers to any more, left behind when an overlay is removed, switched back
to the default icon, or given a replacement of a different file type (which lands
under a different name). It only
ever considers files in the `icons` folder plus the two fixed names 0.3 used
(`overlay-normal.*` / `overlay-fullscreen.*`); nothing else in `%APPDATA%\CatFoil`
is a deletion candidate. Legacy paths still resolve unchanged, so upgrading moves
no files.

Startup is managed by `src/Startup.cs`: "Start with Windows" is an
`HKCU\...\Run\CatFoil` value (non-elevated), re-applied on every launch and save;
"Start elevated at logon" (`StartElevatedOnBoot`) is instead a Task Scheduler task
with highest privileges. The two are mutually exclusive — when the elevated task is
on, the Run key is suppressed so they don't both launch at logon.

Both registrations can be removed with `CatFoil.exe --uninstall-cleanup`
(`Startup.UninstallCleanup`, handled in `Program.Main` before the single-instance
mutex): it deletes the Run value and the task **only when they point at the
invoking EXE**, so an installed copy's uninstall never touches a portable copy's
registration. The installer's `[UninstallRun]` entry invokes it on uninstall.

That delete only works because of the task's security descriptor. Task Scheduler
stamps one at registration reflecting the registering context, and the task must
be registered elevated (`RunLevel=HighestAvailable`), so by default the user's
one ACE is `FR` — read, no delete — and the rest belongs to Administrators and
SYSTEM. An administrator's everyday token carries Administrators **deny-only**,
so the unelevated per-user uninstaller was denied and left an orphan task
pointing at a deleted EXE. `EnableTask` therefore creates the task with
`schtasks` as before and then stamps `Startup.BuildTaskSddl()` over it —
`D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRSD;;;<user SID>)` — adding read + DELETE for
this user and deliberately nothing writable, since write on a
`HighestAvailable` task would let an unelevated process rewrite the action into
elevated code at logon. `RepairTaskSecurity()` re-stamps tasks created by
earlier versions; `TrayAppContext` calls it off-thread at every launch, and it
does nothing unless elevated, the task points at this EXE, and the grant is
actually missing.

Two Task Scheduler behaviours make this narrower than it looks, and both fail
*destructively* — the call is refused only after the task has been altered,
leaving one that needs elevation to remove:

- **Passing an SDDL to `RegisterTask` is rejected** unless the resulting DACL
  still grants the caller write access. Hence create-then-stamp rather than
  registering with the descriptor.
- **A protected DACL (`D:P`, inheritance blocked) is rejected** after the
  inherited ACEs are already stripped. `TrySetTaskSddl` refuses any such SDDL
  outright.


### 5.4 Audio cues — `src/Sounds.cs`, `src/AudioPlayer.cs`, `src/SoundStore.cs`

`Settings` holds three `SoundSetting`s (`LockSound` / `UnlockSound` /
`BlockedSound`): Enabled · Source (System / Custom) · File · Volume. 0.3 had two
bools, `SoundOnLockUnlock` and `SoundOnBlockedKey`; `EnsureSounds()` folds them
in — the first lights *both* halves of the lock/unlock pair — under the same two
rules as the overlay migrations: keyed strictly on `HasValue`, and idempotent, so
a deliberately silenced cue is never switched back on.

**Playback is MCI (`winmm`), not `SoundPlayer`.** `SoundPlayer` is WAV-only with
no volume control, and a decoder library would break the zero-NuGet rule; the
price is a little P/Invoke over MCI's string command interface. Each cue has a
stable alias, so retriggering restarts it rather than layering, while the three
cues can still overlap. **Anything unplayable falls back to the Windows scheme
sound** — a missing file, an undecodable one, or no `winmm` at all — because
silence would look like the feature simply not working. `AudioPlayer.CloseAll()`
runs on exit: MCI handles are process-wide and hold the files open.

`SoundStore` mirrors `IconStore` exactly — imports copied into
`%APPDATA%\CatFoil\sounds` under unique names, paths stored relative to
`Settings.Directory`, and unreferenced files swept when the settings window
closes.
---

## 6. Feature checklist

- Keyboard lock/unlock (mouse stays live); Ctrl+Alt+Del escape hatch.
- Three ways to toggle: main-window button, tray menu, global hotkey.
- Draggable, customizable overlay badges with custom icons, size, and background
  — with live previews — each shown except in, only in, or regardless of
  fullscreen apps.
- First-run welcome tour, re-openable from settings.
- Start-with-Windows, start-minimized, close-to-tray options.
- Free and unrestricted: no license, no trial, no limit on how long a lock lasts.
- Resilience to Windows silently dropping global input after idle/sleep.
- Optional **run-as-administrator** relaunch so the lock also covers elevated windows,
  and optional **silent elevated autostart** at logon (scheduled task, no UAC prompt).
- Optional **auto-lock after inactivity** (idle for N minutes, mouse activity resets it).
- **Timed lock** ("Lock for…" tray submenu) with an auto-unlock countdown.
- **Lifetime statistics** — lock sessions, total locked time, and blocked-key count.
- Optional **sound cues** on lock/unlock and blocked keys (Windows system sounds).
- Single-instance; second launch resurfaces the running one.

---

## 7. What the lock can't block (and elevation)

CatFoil blocks every key-**down** its hook receives while locked, but some things
are out of a user-mode hook's reach:

- **Ctrl + Alt + Del** — the Secure Attention Sequence, handled by Windows itself.
  Never reaches any app. This is the documented escape hatch.
- **Win + L** (lock workstation) — Windows processes this specially; low-level
  keyboard hooks generally can't suppress it. (Effect is just "PC locks.")
- **Xbox Game Bar (Win + G)** and a handful of other shell/UWP feature shortcuts —
  the hook *does* see and swallow these key-downs (confirmed empirically during
  investigation with a diagnostic hook log: `DOWN LWin -> BLOCKED`, `DOWN G -> BLOCKED`),
  yet Windows still activates the feature, because its activation is dispatched off
  the low-level-hook path. Returning `1` from the hook cannot stop it. The only
  reliable blocks are system-level and persistent (disabling the Game Bar app
  entirely, or an Enterprise/IoT keyboard-filter driver), so CatFoil treats Win + G
  as unblockable like the entries above rather than fighting it per lock.
- **The secure desktop** — UAC prompt, lock/login screens, the Ctrl+Alt+Del menu:
  the hook doesn't run there at all.
- **Elevated foreground windows** — a medium-integrity hook can't block keystrokes
  going to a higher-integrity (UAC-elevated) window. This is the gap the **Run as
  administrator** toggle (§2.2) closes: it relaunches CatFoil elevated
  (`src/Elevation.cs` → `runas`), and the new instance waits for the old one to
  exit (`--await-exit <pid>`, handled in `src/Program.cs`) before taking the
  single-instance slot. The relaunch command line also carries the open-windows
  state (`src/RestoreUi.cs`: `--restore-main`, `--restore-settings <page>`), and
  the elevated instance re-opens exactly those — overriding `StartMinimized`,
  which otherwise made the app vanish mid-settings-visit for start-closed users.
  Even elevated, Ctrl+Alt+Del and Win+L remain unblockable.
- **Key-ups always pass through** by design (prevents stuck modifiers) — harmless,
  since a lone key-up can't type. Some special hardware/media/Fn keys may also
  bypass the hook depending on the keyboard. The **mouse is never blocked**.

To make elevation persist, the **Start automatically at logon, elevated** sub-option
(`src/Startup.cs` → a Task Scheduler task with `RunLevel=HighestAvailable`,
`LogonType=InteractiveToken`) starts CatFoil elevated at every logon with **no UAC
prompt**. Creating/removing that task needs an already-elevated process, so the
option is only enabled once "Run as administrator" is on. Without it, elevation is
per-run and would need re-enabling after each reboot.

## 8. Packaging & distribution

CatFoil ships in **two formats every release**, both built from the same self-contained
single-file `CatFoil.exe`: a **portable** EXE (no install) and an **installer**. All mutable
state lives in `%APPDATA%\CatFoil` (settings, overlay icons), never next to the
binary, so the two formats share settings on one machine, an uninstall leaves state intact, and
switching format / reinstalling / upgrading keeps every setting. The **installer** is the
artifact destined for the Microsoft Store; the portable is a GitHub-Releases-only download.

- **Portable** — `scripts/build-portable.ps1` publishes the single-file EXE and copies it out as
  `dist/CatFoil-<ver>-portable.exe`. It runs directly with no admin and nothing to uninstall.

- **Installer** — `installer/CatFoil.iss` (Inno Setup 6) built by
  `scripts/build-installer.ps1`. `PrivilegesRequired=lowest` +
  `PrivilegesRequiredOverridesAllowed=dialog` show a **"Select Install Mode" dialog** so the
  user picks **per-user** (default, **no UAC**, → `%LOCALAPPDATA%\Programs\CatFoil`) or
  **all-users** (asks for admin, → `C:\Program Files\CatFoil`). A **truly silent install is
  `/VERYSILENT /CURRENTUSER`**: `/VERYSILENT` on its own still shows the install-mode dialog
  (verified 2026-07-27), because `PrivilegesRequiredOverridesAllowed=dialog` asks before the
  silent flag is honoured. `ArchitecturesInstallIn64BitMode=x64compatible` makes the per-machine
  path land in the real 64-bit Program Files (the payload is win-x64). `{autopf}`, `{group}`,
  and `{autodesktop}` resolve to the matching per-user/common locations automatically, so both
  modes get a Start-menu shortcut and an Apps & Features uninstaller.
  `AppMutex=CatFoil-SingleInstance` (matching the app's single-instance mutex in
  `src/Program.cs`) lets the installer detect and close a running instance so it can replace
  the self-locking EXE without a reboot. The `asInvoker` manifest is unchanged — the app still
  self-elevates on demand (§7), so even a no-admin per-user install can block elevated windows.
  The post-install launch uses `runasoriginaluser` so an all-users (elevated) install still
  starts CatFoil as the normal user. On uninstall, an `[UninstallRun]` entry runs the
  still-installed EXE with `--uninstall-cleanup` (before file removal) so the runtime-created
  startup registrations — the HKCU `Run` value and the elevated scheduled task — don't survive
  as orphans firing against a deleted EXE (path-guarded; see §5). Known limitation: a
  per-machine uninstall run by a *different* admin account can't reach the original user's
  HKCU/task.

The build scripts share `scripts/_common.ps1` (publish, version, locate ISCC), and
`scripts/build-release.ps1` — the per-release command — publishes **once** and emits both the
portable EXE and the installer, so the two are byte-for-byte the same binary and can never
drift. The version comes from `<Version>` in `CatFoil.csproj` (currently `0.4.0`) so the EXE
metadata and every artifact filename always match. The installer is **offline** (payload
bundled) and **silent-capable** (`/VERYSILENT`), which are the two hard requirements for the
Microsoft Store's **MSI/EXE submission path** — so the same installer can be listed on the
Store without repackaging as MSIX. The remaining Store prerequisite is **code-signing** the
setup and payload with a cert chaining to a Microsoft-Trusted-Root CA (e.g. Azure Trusted
Signing); the Store does not auto-update MSI/EXE apps, so updates stay the app's/installer's
responsibility. CatFoil would be listed as a **free** app.
