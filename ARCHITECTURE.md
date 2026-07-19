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
- **Exit** button (top-left, soft red tint) — raises `ExitRequested`.
- **Settings** button (top-left, next to Exit) — raises `SettingsRequested`.
- **Hotkey badge** (bottom-left, above the lock button) — a custom-drawn control
  (`HotkeyBadge`) rendering the active hotkey as 3D keycaps joined by "+". Hidden
  when the hotkey is disabled.

Behaviors:
- **Close-to-tray**: `FormClosing` cancels a user close and hides, unless
  `AllowClose` is set (real exit) or the tray-on-close setting is off.
- **Timed-lock countdown**: `ShowLockCountdown` appends "Auto-unlock in m:ss"
  while a user-chosen "Lock for N minutes" runs.

### 2.2 Settings window — `src/SettingsForm.cs`
A fixed-size dialog (512×636), lazily created and reused by the tray. Groups:

- **General**: checkboxes — Hide to tray on close · Start hidden in tray ·
  Start CatFoil when Windows starts · Show the cat overlay while locked · **Run as
  administrator (also block elevated windows)** with an indented sub-option **Start
  automatically at logon, elevated (no prompt)** — plus a **"Customize overlay…"**
  button opening the overlay menu. Checking "Run as administrator" relaunches
  CatFoil elevated (UAC prompt) so its hook can also block elevated foreground
  windows; if already elevated the box is checked and disabled. The elevated-logon
  sub-option (enabled only while elevated) creates a Task Scheduler task that starts
  CatFoil elevated at logon with no prompt. See §7.
- **Hotkey**: enable checkbox · a click-to-capture hotkey box ("press keys") ·
  a **Multi-key chord** checkbox (with a tooltip explaining the leak-through
  trade-off). Capture logic differs for classic vs chord mode.
- **Auto-lock**: enable checkbox + a minutes selector — locks after that long
  with no keyboard or mouse activity.
- **Sounds**: two checkboxes — play a sound on lock/unlock · play a (throttled)
  sound when a key is blocked while locked. Both use the user's Windows system
  sounds (tooltip points at the Windows sound settings if the mapped events
  are "(None)").

Bottom row: **Welcome tour…** · **Apply** (persist without closing) ·
**Save** (persist + close) · **Cancel**. Save/Apply both call `PersistSettings()`,
which writes settings and raises `SettingsSaved` so the tray applies changes live.

### 2.3 Overlay customization menu — `src/OverlaySettingsForm.cs`
A dialog (652×746) opened from Settings → "Customize overlay…". Two mirrored
**state editors** (`StateEditor`):
- **Normal (no fullscreen app)**
- **When a fullscreen app is running**

Each editor has: show-in-this-state toggle · Default cat / Custom image radios +
**Browse…** · size slider (32–256 px) · show-background-box toggle · and a
**checkerboard live preview** (`PreviewBox`) that paints via the shared
`OverlayRenderer` at true 1:1 size. On **OK**, any newly chosen custom image is
copied into `%APPDATA%\CatFoil\` as `overlay-normal.<ext>` / `overlay-fullscreen.<ext>`,
the two `OverlayStateSettings` are written, and `SettingsSaved` is raised.

### 2.4 Welcome window — `src/WelcomeForm.cs`
Shown once on first launch (flag `Settings.WelcomeShown`), and re-openable from
Settings → "Welcome tour…". A scrolling tour (auto-sized to content, since the
hotkey string is variable): what CatFoil does, Locking, Unlocking, the cat badge,
and the tray icon. Single **Get started** button.

### 2.5 Locked overlay badge — `src/OverlayForm.cs`
A small, borderless, always-on-top **layered window** (WS_EX_LAYERED +
`UpdateLayeredWindow` pushing a 32bpp ARGB bitmap) shown while locked. It never
steals focus (WS_EX_NOACTIVATE, `ShowWithoutActivation`). Features:
- **Per-state appearance**: a 1-second poll picks Normal vs Fullscreen state via
  `ForegroundIsFullscreen()` and shows / hides / resizes accordingly. Re-compositing
  the layered window is skipped unless the resolved icon, size, countdown, or flash
  actually changed since the last paint, so the poll is nearly free while nothing moves.
- **Draggable** (position saved, clamped to the virtual screen); a **click**
  (no drag) opens the main window.
- **Countdown text** during a timed lock (GDI+ `DrawString` so glyphs carry
  alpha on the layered surface).
- **Red flash** on a blocked keypress (`FlashBlockedKey`).
- **Manual tooltip** shown on hover (auto tooltips don't work on never-activated
  windows).
Painting is shared with the settings preview through `src/OverlayRenderer.cs`.

---

## 3. System tray icon & menu

Owned by `TrayAppContext`. The `NotifyIcon` uses the app icon; its tooltip text
tracks state ("CatFoil — keyboard active" / "— KEYBOARD LOCKED").

- **Double-click** the tray icon → open the main window.
- **Right-click** → context menu:
  1. **Open CatFoil** (bold default) → show main window
  2. **Lock Keyboard** / **Unlock Keyboard** (label toggles with state)
  3. **Lock for…** submenu — 5/15/30/60 minutes, then auto-unlock
  4. **Statistics…** → lifetime lock stats
  5. **Settings…** → open the settings window
  6. — separator —
  7. **Exit** → shut the app down

---

## 4. Locking engine

| Piece | File | Role |
| --- | --- | --- |
| Low-level hook | `src/KeyboardHook.cs` | `WH_KEYBOARD_LL`. While **locked**, swallows every key-**down** (returns 1); key-**ups** always pass through so Windows' modifier state never desyncs. Mouse is untouched (no mouse hook). |
| Unlock while locked | `src/KeyboardHook.cs` | RegisterHotKey can't fire while keys are swallowed, so the unlock combo is detected **inside** the hook, using modifier state the hook **tracks itself** (`TrackModifier`) — never `GetAsyncKeyState`, which is blind to swallowed keys. |
| Classic hotkey | `src/HotkeyManager.cs` | `RegisterHotKey` (with `MOD_NOREPEAT`) on a `NativeWindow`; fires only while unlocked. This is the sole lock trigger in classic mode. |
| Chord hotkey | `src/KeyboardHook.cs` | Opt-in "Alt + C + F"-style chord, detected in **both** lock states inside the hook (`CompletesChord`), since RegisterHotKey can't express multi-key chords. The completing keystroke is swallowed; earlier chord keys leak to the focused app while unlocked (documented trade-off). |
| Toggle plumbing | `src/TrayAppContext.cs` | `ToggleLock` (400 ms debounce, since lock and unlock use the same keys) → `SetLocked`. Sets hook lock state and updates UI/tray/overlay. |
| Idle resilience | `src/TrayAppContext.cs` | A 60 s **watchdog** plus power-resume / session-unlock handlers re-arm the hotkey and (while unlocked) reinstall the hook, because Windows silently drops both after long idle or sleep. |

`Ctrl + Alt + Del` cannot be blocked (Windows reserves it) and is documented as
the always-available escape hatch.

---

## 5. Settings model — `src/Settings.cs`

JSON at `%APPDATA%\CatFoil\settings.json` (`Keys` serialized as string flags).
Notable fields: `Hotkey` (default **Alt+G**), `HotkeyEnabled`, `UseChordHotkey`
(default off) + `ChordModifiers`/`ChordKeys`, `MinimizeToTrayOnClose`,
`StartWithWindows`, `StartElevatedOnBoot`, `StartMinimized`, `ShowOverlay`, `WelcomeShown`,
`OverlayPosition`, per-state `OverlayNormal`/`OverlayFullscreen`
(`OverlayStateSettings`: Visible, UseCustomIcon, CustomIconFile, Size 32–256,
ShowBackground), plus `AutoLockEnabled`/`AutoLockMinutes`. Corrupt files fall back
to defaults.

Startup is managed by `src/Startup.cs`: "Start with Windows" is an
`HKCU\...\Run\CatFoil` value (non-elevated), re-applied on every launch and save;
"Start elevated at logon" (`StartElevatedOnBoot`) is instead a Task Scheduler task
with highest privileges. The two are mutually exclusive — when the elevated task is
on, the Run key is suppressed so they don't both launch at logon.

---

## 6. Feature checklist

- Keyboard lock/unlock (mouse stays live); Ctrl+Alt+Del escape hatch.
- Three ways to toggle: main-window button, tray menu, global hotkey.
- Classic single-combo hotkey **or** opt-in multi-key chord.
- Draggable, per-state (normal vs fullscreen) customizable overlay badge with
  custom icons, size, and background — with live previews.
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
  single-instance slot. Even elevated, Ctrl+Alt+Del and Win+L remain unblockable.
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
  **all-users** (asks for admin, → `C:\Program Files\CatFoil`); a `/VERYSILENT` install takes
  the per-user path. `ArchitecturesInstallIn64BitMode=x64compatible` makes the per-machine
  path land in the real 64-bit Program Files (the payload is win-x64). `{autopf}`, `{group}`,
  and `{autodesktop}` resolve to the matching per-user/common locations automatically, so both
  modes get a Start-menu shortcut and an Apps & Features uninstaller.
  `AppMutex=CatFoil-SingleInstance` (matching the app's single-instance mutex in
  `src/Program.cs`) lets the installer detect and close a running instance so it can replace
  the self-locking EXE without a reboot. The `asInvoker` manifest is unchanged — the app still
  self-elevates on demand (§7), so even a no-admin per-user install can block elevated windows.
  The post-install launch uses `runasoriginaluser` so an all-users (elevated) install still
  starts CatFoil as the normal user.

The build scripts share `scripts/_common.ps1` (publish, version, locate ISCC), and
`scripts/build-release.ps1` — the per-release command — publishes **once** and emits both the
portable EXE and the installer, so the two are byte-for-byte the same binary and can never
drift. The version comes from `<Version>` in `CatFoil.csproj` (currently `0.3.0`) so the EXE
metadata and every artifact filename always match. The installer is **offline** (payload
bundled) and **silent-capable** (`/VERYSILENT`), which are the two hard requirements for the
Microsoft Store's **MSI/EXE submission path** — so the same installer can be listed on the
Store without repackaging as MSIX. The remaining Store prerequisite is **code-signing** the
setup and payload with a cert chaining to a Microsoft-Trusted-Root CA (e.g. Azure Trusted
Signing); the Store does not auto-update MSI/EXE apps, so updates stay the app's/installer's
responsibility. CatFoil would be listed as a **free** app.
