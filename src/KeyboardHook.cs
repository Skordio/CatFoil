using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Global low-level keyboard hook. While locked it swallows every key-down
/// (key-ups pass through so Windows' modifier state never desyncs),
/// except the configured unlock combo which raises <see cref="UnlockComboPressed"/>
/// instead — RegisterHotKey never fires while we're swallowing keys, so the
/// unlock path has to live inside the hook itself.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;

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

    // Keep a strong reference to the delegate so the GC never collects it
    // while the hook is still installed (a classic crash-causing bug).
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    // Modifier state tracked from the raw events the hook sees. We can't ask
    // Windows (GetAsyncKeyState): swallowed key-downs never reach the OS key
    // state tables, so while locked it reports held modifiers as "up".
    private bool _lCtrl, _rCtrl, _lAlt, _rAlt, _lShift, _rShift;

    public bool IsLocked { get; private set; }

    /// <summary>Combo that unlocks while locked. Keys.None disables it.</summary>
    public Keys UnlockCombo { get; set; } = Keys.None;

    /// <summary>Modifiers required by the chord (used with SetChordKeys).</summary>
    public Keys ChordModifiers { get; set; }

    // Chord mode: several normal keys held together. Detected in BOTH lock
    // states, since RegisterHotKey can't express multi-key chords.
    private Keys[] _chordKeys = Array.Empty<Keys>();
    private bool[] _chordDown = Array.Empty<bool>();

    /// <summary>Non-modifier keys of the chord. Empty disables chord mode.</summary>
    public void SetChordKeys(Keys[] keys)
    {
        // Leave the held-key state alone when the chord is unchanged. Otherwise a
        // re-arm (the idle watchdog re-applies settings every 60s) that lands
        // mid-chord would clear the keys already held and drop the chord.
        if (SameChord(keys)) return;
        _chordKeys = keys;
        _chordDown = new bool[keys.Length];
    }

    private bool SameChord(Keys[] keys)
    {
        if (_chordKeys.Length != keys.Length) return false;
        for (int i = 0; i < keys.Length; i++)
            if (_chordKeys[i] != keys[i]) return false;
        return true;
    }

    // Raised from the hook callback (UI thread, but mid-hook) — handlers must
    // defer real work with BeginInvoke so the hook returns fast.
    public event Action? BlockedKeyPress;
    public event Action? UnlockComboPressed;
    public event Action? ChordPressed;

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

    /// <summary>True while a hook handle is held (not a liveness guarantee).</summary>
    public bool IsInstalled => _hookId != IntPtr.Zero;

    /// <summary>
    /// Tears down and re-adds the hook. Windows silently removes a low-level
    /// hook whose callback overruns LowLevelHooksTimeout — which can happen on
    /// the first keypress after the process idles and its pages are trimmed —
    /// and leaves us no signal: our stored handle stays non-null but dead.
    /// A caller can re-arm periodically so the hook survives long idle periods.
    /// </summary>
    public bool Reinstall(out int win32Error)
    {
        // Install the fresh hook FIRST, then drop the old one only if that
        // succeeded. Unhooking first and then failing to re-install would leave
        // us with no hook at all — locking would silently stop swallowing keys.
        IntPtr old = _hookId;
        if (!Install(out win32Error))
        {
            _hookId = old;   // keep the existing (possibly still-live) hook
            return false;
        }
        if (old != IntPtr.Zero)
            UnhookWindowsHookEx(old);
        return true;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp   = msg is WM_KEYUP or WM_SYSKEYUP;

            // KBDLLHOOKSTRUCT.vkCode is the first DWORD.
            var vk = (Keys)Marshal.ReadInt32(lParam);

            if (isDown || isUp)
                TrackModifier(vk, isDown);

            if (isDown && CompletesChord(vk))
            {
                ChordPressed?.Invoke();
                return (IntPtr)1;   // swallow the keystroke that completed the chord
            }
            if (isUp)
                ReleaseChordKey(vk);

            if (IsLocked && isDown)
            {
                if (MatchesUnlockCombo(vk))
                    UnlockComboPressed?.Invoke();
                else
                    BlockedKeyPress?.Invoke();

                // Returning 1 swallows the keystroke. The mouse is untouched
                // because we never installed a mouse hook.
                return (IntPtr)1;
            }

            // Key-UPs pass through even while locked. Swallowing them desyncs
            // Windows' key state: a modifier held while locking (e.g. the
            // Ctrl+Alt of the hotkey) would never be seen released, leaving
            // Ctrl/Alt/Shift "stuck down" after unlock. A lone key-up can't
            // type or trigger shortcuts.
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void TrackModifier(Keys vk, bool down)
    {
        switch (vk)
        {
            case Keys.LControlKey: _lCtrl  = down; break;
            case Keys.RControlKey: _rCtrl  = down; break;
            case Keys.LMenu:       _lAlt   = down; break;
            case Keys.RMenu:       _rAlt   = down; break;
            case Keys.LShiftKey:   _lShift = down; break;
            case Keys.RShiftKey:   _rShift = down; break;
        }
    }

    // A chord fires exactly once: on the key-down that completes it while the
    // required modifiers (and no others) are held. Key-repeats don't re-fire.
    private bool CompletesChord(Keys vk)
    {
        int idx = Array.IndexOf(_chordKeys, vk);
        if (idx < 0) return false;

        bool wasDown = _chordDown[idx];
        _chordDown[idx] = true;
        if (wasDown) return false;   // key-repeat of an already-held key

        foreach (bool down in _chordDown)
            if (!down) return false;

        return ModifierMatches(ChordModifiers, Keys.Control, _lCtrl  || _rCtrl)
            && ModifierMatches(ChordModifiers, Keys.Alt,     _lAlt   || _rAlt)
            && ModifierMatches(ChordModifiers, Keys.Shift,   _lShift || _rShift);
    }

    private void ReleaseChordKey(Keys vk)
    {
        int idx = Array.IndexOf(_chordKeys, vk);
        if (idx >= 0) _chordDown[idx] = false;
    }

    private bool MatchesUnlockCombo(Keys vk)
    {
        Keys combo = UnlockCombo;
        if (combo == Keys.None || (combo & Keys.KeyCode) != vk) return false;
        return ModifierMatches(combo, Keys.Control, _lCtrl  || _rCtrl)
            && ModifierMatches(combo, Keys.Alt,     _lAlt   || _rAlt)
            && ModifierMatches(combo, Keys.Shift,   _lShift || _rShift);
    }

    // Required modifiers must be down, non-required ones up, so e.g.
    // Ctrl+Shift+Alt+L doesn't trigger a Ctrl+Alt+L combo.
    private static bool ModifierMatches(Keys combo, Keys flag, bool down)
    {
        bool required = (combo & flag) != 0;
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
