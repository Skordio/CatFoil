using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// RegisterHotKey wrapper on a message-only window. Fires while the keyboard
/// is unlocked; while locked the hook swallows keys first, so the unlock side
/// of the toggle is handled inside <see cref="KeyboardHook"/> instead.
/// </summary>
public sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 1;

    private const uint MOD_ALT      = 0x0001;
    private const uint MOD_CONTROL  = 0x0002;
    private const uint MOD_SHIFT    = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private bool _registered;

    public event Action? HotkeyPressed;

    public HotkeyManager()
    {
        CreateHandle(new CreateParams());
    }

    public bool Register(Keys combo)
    {
        Unregister();
        uint mods = MOD_NOREPEAT;
        if (combo.HasFlag(Keys.Control)) mods |= MOD_CONTROL;
        if (combo.HasFlag(Keys.Alt)) mods |= MOD_ALT;
        if (combo.HasFlag(Keys.Shift)) mods |= MOD_SHIFT;
        _registered = RegisterHotKey(Handle, HOTKEY_ID, mods, (uint)(combo & Keys.KeyCode));
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(Handle, HOTKEY_ID);
            _registered = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID)
            HotkeyPressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
