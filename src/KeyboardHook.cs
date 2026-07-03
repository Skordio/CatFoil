using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Global low-level keyboard hook. While locked it swallows every keystroke,
/// except the configured unlock combo which raises <see cref="UnlockComboPressed"/>
/// instead — RegisterHotKey never fires while we're swallowing keys, so the
/// unlock path has to live inside the hook itself.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_SYSKEYDOWN  = 0x0104;

    private const int VK_SHIFT   = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU    = 0x12;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Keep a strong reference to the delegate so the GC never collects it
    // while the hook is still installed (a classic crash-causing bug).
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    public bool IsLocked { get; private set; }

    /// <summary>Combo that unlocks while locked. Keys.None disables it.</summary>
    public Keys UnlockCombo { get; set; } = Keys.None;

    // Raised from the hook callback (UI thread, but mid-hook) — handlers must
    // defer real work with BeginInvoke so the hook returns fast.
    public event Action? BlockedKeyPress;
    public event Action? UnlockComboPressed;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public bool Install(out int win32Error)
    {
        using Process cur = Process.GetCurrentProcess();
        using ProcessModule mod = cur.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod.ModuleName), 0);
        win32Error = _hookId == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        return _hookId != IntPtr.Zero;
    }

    public void Lock() => IsLocked = true;
    public void Unlock() => IsLocked = false;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsLocked)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                // KBDLLHOOKSTRUCT.vkCode is the first DWORD.
                var vk = (Keys)Marshal.ReadInt32(lParam);
                if (MatchesUnlockCombo(vk))
                    UnlockComboPressed?.Invoke();
                else
                    BlockedKeyPress?.Invoke();
            }

            // Returning 1 swallows the keystroke. The mouse is untouched
            // because we never installed a mouse hook.
            return (IntPtr)1;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool MatchesUnlockCombo(Keys vk)
    {
        Keys combo = UnlockCombo;
        if (combo == Keys.None || (combo & Keys.KeyCode) != vk) return false;
        return ModifierMatches(combo, Keys.Control, VK_CONTROL)
            && ModifierMatches(combo, Keys.Alt, VK_MENU)
            && ModifierMatches(combo, Keys.Shift, VK_SHIFT);
    }

    // Required modifiers must be down, non-required ones up, so e.g.
    // Ctrl+Shift+Alt+L doesn't trigger a Ctrl+Alt+L combo.
    private static bool ModifierMatches(Keys combo, Keys flag, int vk)
    {
        bool required = (combo & flag) != 0;
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        return required == down;
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}
