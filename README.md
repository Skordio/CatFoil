# CatFoil 🐱

Foil your cat. CatFoil is a small Windows tray utility that locks your keyboard with one click (or a hotkey) — so a cat walking across your desk (or a toddler slapping the keys) can't type, trigger shortcuts, or close your windows. The mouse keeps working the whole time, so *you* can always click your way back to control.

## Features

- **One-click lock** from the main window, the tray menu, or a global hotkey (default **Alt+G** — rebindable, and optionally a multi-key chord like Alt + C + F).
- **Lives in the system tray** with a cat icon; closing the window hides it to the tray instead of exiting.
- **On-screen cat overlay** while locked: a small draggable badge that reminds you the keyboard is off. Hover it for an explanation, click it to open CatFoil. It stays out of the way of fullscreen apps (videos, games) and reappears afterwards.
- **Blocked-key feedback**: pressing a key while locked flashes the overlay badge red, and restores the main window if you'd otherwise have no way back in.
- **Timed lock** ("Lock for…" in the tray menu) that auto-unlocks after 5/15/30/60 minutes, and optional **auto-lock** after a stretch of no keyboard or mouse activity.
- **Settings** (saved to `%APPDATA%\CatFoil\settings.json`): hotkey, overlay appearance, auto-lock, hide-to-tray, start hidden, and start with Windows (a registry Run entry the app manages itself).
- **Two ways to get it**: a **portable** single-file EXE that runs with no install, or a **one-click installer** (per-user with no admin, or all-users) that adds a Start-menu shortcut and an uninstaller. Either way your settings live in `%APPDATA%`, so switching formats, upgrading, or reinstalling keeps everything.

## Installing

Every release on the [Releases](https://github.com/Skordio/CatFoil/releases) page ships in two
formats — pick whichever you prefer:

**Portable** — download `CatFoil-<version>-portable.exe` and run it. No install, no admin, nothing
to uninstall; just delete the EXE when you're done. Your settings still live in `%APPDATA%\CatFoil`.

**Installer** — download `CatFoil-Setup-<version>.exe` and run it. Setup asks how you want to install:

- **Install for me only** (default, **no administrator prompt**) → `%LOCALAPPDATA%\Programs\CatFoil`.
- **Install for all users** (asks for admin) → `C:\Program Files\CatFoil`.

Either way it adds a Start-menu shortcut and registers an uninstaller (Apps & Features →
CatFoil). The app self-elevates only when it actually needs to block elevated windows, so the
per-user install can still do everything. Uninstalling removes the app but keeps your settings
in `%APPDATA%\CatFoil`. (CatFoil is also headed to the Microsoft Store via the installer.)

## How it works

When locked, CatFoil installs a system-wide [low-level keyboard hook](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) (`SetWindowsHookEx` with `WH_KEYBOARD_LL`) and swallows every keystroke before it reaches any application — except the unlock hotkey, which is recognized inside the hook itself. No mouse hook is ever installed, so unlocking is always one click away.

**Notes and limitations**

- No administrator rights are required (the app manifest requests `asInvoker`).
- Secure desktop keys like **Ctrl+Alt+Del** are handled by Windows before low-level hooks and cannot be blocked — that's by design, and it's also your emergency escape hatch.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build; the built app needs only the .NET 8 Desktop Runtime)

## Building and running (development)

Clone the repo and run it straight from the source tree:

```powershell
git clone https://github.com/Skordio/CatFoil.git
cd CatFoil
dotnet run
```

Other useful commands:

```powershell
dotnet build                 # debug build → bin/Debug/net8.0-windows/CatFoil.exe
dotnet build -c Release      # release build → bin/Release/net8.0-windows/CatFoil.exe
```

To build the distributable artifacts, install
[Inno Setup 6](https://jrsoftware.org/isinfo.php)
(`winget install JRSoftware.InnoSetup`) and run one of the build scripts:

```powershell
.\scripts\build-release.ps1     # both formats from a single publish (use this for a release)
.\scripts\build-portable.ps1    # just dist\CatFoil-<version>-portable.exe
.\scripts\build-installer.ps1   # just dist\CatFoil-Setup-<version>.exe
```

All three publish the same self-contained single-file EXE (version comes from `<Version>` in
`CatFoil.csproj`); `build-release.ps1` publishes once and emits both the portable EXE and the
installer, so they're guaranteed to be the same binary. The installer (`installer\CatFoil.iss`)
is offline and supports silent install (`/VERYSILENT`), so it's ready for the Microsoft Store's
MSI/EXE submission path — that path additionally requires code-signing the setup and payload with
a Microsoft-Trusted-Root cert.

## Project layout

| Path | Purpose |
| --- | --- |
| `src/Program.cs` | Entry point; single-instance mutex (a second launch surfaces the first). |
| `src/TrayAppContext.cs` | App shell: tray icon/menu, lock state, timed lock, wiring between everything. |
| `src/KeyboardHook.cs` | The `WH_KEYBOARD_LL` hook; swallows keys while locked, detects the unlock combo. |
| `src/HotkeyManager.cs` | `RegisterHotKey` wrapper for locking while the keyboard is live. |
| `src/MainForm.cs` | The lock/unlock window (hotkey badge, timed-lock countdown). |
| `src/OverlayForm.cs` | The draggable locked-state badge + fullscreen detection. |
| `src/SettingsForm.cs` | Settings UI (general, hotkey, auto-lock). |
| `src/Settings.cs` | JSON settings in `%APPDATA%\CatFoil`. |
| `assets/cat.ico` | Placeholder cat icon (EXE, tray, overlay) — replace with real art anytime. |
| `CatFoil.csproj` | SDK-style project file (WinForms, `net8.0-windows`, no external dependencies). |
| `app.manifest` | Requests `asInvoker` (no UAC prompt). |
| `installer/CatFoil.iss` | Inno Setup script — per-user/all-users installer, Store-ready (offline + silent). |
| `scripts/_common.ps1` | Shared build helpers (publish, version, locate ISCC) dot-sourced by the build scripts. |
| `scripts/build-release.ps1` | Publishes once and emits both the portable EXE and the installer to `dist/`. |
| `scripts/build-portable.ps1` | Publishes the portable single-file EXE to `dist/`. |
| `scripts/build-installer.ps1` | Publishes the single-file EXE and compiles the installer to `dist/`. |
