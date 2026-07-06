# CatFoil 🐱🔒

Foil your cat. CatFoil is a small Windows tray utility that locks your keyboard with one click (or a hotkey) — so a cat walking across your desk (or a toddler slapping the keys) can't type, trigger shortcuts, or close your windows. The mouse keeps working the whole time, so *you* can always click your way back to control.

## Features

- **One-click lock** from the main window, the tray menu, or a global hotkey (default **Ctrl+Alt+L** — rebindable).
- **Lives in the system tray** with a cat icon; closing the window hides it to the tray instead of exiting.
- **On-screen cat overlay** while locked: a small draggable badge that reminds you the keyboard is off. Hover it for an explanation, click it to open CatFoil. It stays out of the way of fullscreen apps (videos, games) and reappears afterwards.
- **Blocked-key feedback**: pressing a key while locked flashes the window/overlay red, and restores the window if you'd otherwise have no way back in.
- **Settings** (saved to `%APPDATA%\CatFoil\settings.json`): hotkey, overlay, hide-to-tray, start hidden, start with Windows (a registry Run entry the app manages itself).
- **Install or run portable**: a one-click per-user installer (no admin) that adds a Start-menu shortcut and an uninstaller — or just run the single self-contained EXE with nothing installed. Your settings live in `%APPDATA%` either way, so switching between them keeps everything.

## Installing

**Option 1 — Installer (recommended).** Download `CatFoil-Setup-<version>.exe` from the
[Releases](https://github.com/Skordio/CatFoil/releases) page and run it. It installs
per-user to `%LOCALAPPDATA%\Programs\CatFoil` with **no administrator prompt**, adds a
Start-menu shortcut, and registers an uninstaller (Apps & Features → CatFoil). The app
self-elevates only when it actually needs to block elevated windows. Uninstalling removes
the app but keeps your settings in `%APPDATA%\CatFoil`.

**Option 2 — Portable.** Download the standalone `CatFoil.exe` and run it from anywhere;
nothing is installed. Same app, same settings location.

## How it works

When locked, CatFoil installs a system-wide [low-level keyboard hook](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) (`SetWindowsHookEx` with `WH_KEYBOARD_LL`) and swallows every keystroke before it reaches any application — except the unlock hotkey, which is recognized inside the hook itself. No mouse hook is ever installed, so unlocking is always one click away.

**Notes and limitations**

- No administrator rights are required (the app manifest requests `asInvoker`).
- Secure desktop keys like **Ctrl+Alt+Del** are handled by Windows before low-level hooks and cannot be blocked — that's by design, and it's also your emergency escape hatch.
- Windows may silently remove a hook that takes too long to respond; if keys start leaking through, unlock and re-lock.

## License keys

CatFoil is source-available and free to build yourself. The prebuilt EXE is free to use with one restriction: each lock session ends after **30 minutes** (with a countdown warning at 2 minutes remaining). A one-time license key — sold via Lemon Squeezy for about the price of a coffee — removes the limit. Enter the key under **Settings → License**; activation happens once online and works offline afterwards.

There's no DRM beyond that. If you'd rather clone the repo and remove the check, you can — but if CatFoil saves your work from your cat, consider buying a license anyway. 🐾

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

To produce a self-contained single-file executable that runs on machines without .NET installed:

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The output lands in `bin/Release/net8.0-windows/win-x64/publish/CatFoil.exe`.

To build the **per-user installer**, install [Inno Setup 6](https://jrsoftware.org/isinfo.php)
(`winget install JRSoftware.InnoSetup`) and run:

```powershell
.\scripts\build-installer.ps1
```

It publishes the self-contained single-file EXE and compiles `installer\CatFoil.iss` into
`dist\CatFoil-Setup-<version>.exe` (version comes from `<Version>` in `CatFoil.csproj`).
The installer is offline and supports silent install (`/VERYSILENT`), so it's ready for the
Microsoft Store's MSI/EXE submission path — that path additionally requires code-signing the
setup and payload with a Microsoft-Trusted-Root cert.

### Dev tips

- Set the `CATFOIL_TRIAL_SECONDS` environment variable (e.g. `180`) before launching to shrink the 30-minute free session for testing the countdown/expiry flow.
- The Lemon Squeezy product URL lives in `src/Licensing/LemonSqueezyProvider.cs` (`BuyUrl`) — it's a placeholder until the store product exists.

## Project layout

| Path | Purpose |
| --- | --- |
| `src/Program.cs` | Entry point; single-instance mutex (a second launch surfaces the first). |
| `src/TrayAppContext.cs` | App shell: tray icon/menu, lock state, trial timer, wiring between everything. |
| `src/KeyboardHook.cs` | The `WH_KEYBOARD_LL` hook; swallows keys while locked, detects the unlock combo. |
| `src/HotkeyManager.cs` | `RegisterHotKey` wrapper for locking while the keyboard is live. |
| `src/MainForm.cs` | The lock/unlock window (flash feedback, trial countdown, buy link). |
| `src/OverlayForm.cs` | The draggable locked-state badge + fullscreen detection. |
| `src/SettingsForm.cs` | Settings UI including license activation. |
| `src/Settings.cs` | JSON settings in `%APPDATA%\CatFoil`. |
| `src/Licensing/` | `ILicenseProvider` + Lemon Squeezy implementation (Store build can slot in later). |
| `assets/cat.ico` | Placeholder cat icon (EXE, tray, overlay) — replace with real art anytime. |
| `CatFoil.csproj` | SDK-style project file (WinForms, `net8.0-windows`, no external dependencies). |
| `app.manifest` | Requests `asInvoker` (no UAC prompt). |
| `installer/CatFoil.iss` | Inno Setup script — per-user, no-admin installer, Store-ready (offline + silent). |
| `scripts/build-installer.ps1` | Publishes the single-file EXE and compiles the installer to `dist/`. |
