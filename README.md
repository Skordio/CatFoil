# CatFoil 🐱🔒

Foil your cat. CatFoil is a tiny Windows utility that locks your keyboard with one click — so a cat walking across your desk (or a toddler slapping the keys) can't type, trigger shortcuts, or close your windows. The mouse keeps working the whole time, so *you* can always click **Unlock Keyboard** to get control back.

## How it works

When you click **Lock Keyboard**, CatFoil installs a system-wide [low-level keyboard hook](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) (`SetWindowsHookEx` with `WH_KEYBOARD_LL`) and swallows every keystroke before it reaches any application. While locked:

- The window grows, jumps to the center of the screen, and stays on top.
- Every blocked keypress makes the window flash red twice, so you can see the lock is doing its job.
- If the window was minimized, a blocked keypress restores it — the unlock button is never out of reach.
- The mouse is untouched (no mouse hook is installed), so unlocking is always one click away.

Closing the window removes the hook and returns the keyboard to normal.

**Notes and limitations**

- No administrator rights are required (the app manifest requests `asInvoker`).
- Secure desktop keys like **Ctrl+Alt+Del** are handled by Windows before low-level hooks and cannot be blocked — that's by design, and it's also your emergency escape hatch.
- Windows may silently remove a hook that takes too long to respond (see `LowLevelHooksTimeout`); if keys start leaking through, unlock and re-lock.

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

## Project layout

| File | Purpose |
| --- | --- |
| `Program.cs` | The entire app: entry point, main form, Win32 hook plumbing, and lock/unlock UI. |
| `CatFoil.csproj` | SDK-style project file (WinForms, `net8.0-windows`, no external dependencies). |
| `app.manifest` | Requests `asInvoker` (no UAC prompt) and PerMonitorV2 DPI awareness. |
