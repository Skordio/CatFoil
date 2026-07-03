using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CatFoil;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    // ---- Win32 hook plumbing ----
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
    private bool _locked;

    // ---- UI ----
    private readonly Label _status = new();
    private readonly Button _toggle = new();
    private readonly System.Windows.Forms.Timer _flash = new();
    private bool _flashOn;
    private int _flashTicks;   // remaining on/off transitions in the current burst

    private static readonly Size UnlockedSize = new(420, 260);
    private static readonly Size LockedSize   = new(760, 480);

    public MainForm()
    {
        _proc = HookCallback;

        // --- Form ---
        Text = "CatFoil";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = false;
        ClientSize = UnlockedSize;
        BackColor = Color.FromArgb(245, 245, 245);

        // --- Status label (fills the window) ---
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        _status.ForeColor = Color.FromArgb(0, 130, 0);
        _status.Text = "Keyboard is ACTIVE";

        // --- Toggle button (docked to the bottom, big enough to mouse-click) ---
        _toggle.Dock = DockStyle.Bottom;
        _toggle.Height = 64;
        _toggle.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
        _toggle.Text = "Lock Keyboard";
        _toggle.Click += (_, _) => ToggleLock();
        // Stop the button from grabbing keyboard focus / space-bar activation.
        _toggle.TabStop = false;

        Controls.Add(_status);
        Controls.Add(_toggle);

        // --- Flash timer: a short 2-blink reaction to a blocked key ---
        _flash.Interval = 120;
        _flash.Tick += (_, _) => FlashTick();

        Load     += (_, _) => InstallHook();
        FormClosed += (_, _) => RemoveHook();
    }

    // ---------------------------------------------------------------
    // Hook lifecycle
    // ---------------------------------------------------------------
    private void InstallHook()
    {
        using Process cur = Process.GetCurrentProcess();
        using ProcessModule mod = cur.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
        {
            MessageBox.Show(
                "Failed to install the keyboard hook (error " + Marshal.GetLastWin32Error() + ").",
                "CatFoil", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RemoveHook()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    // This runs on the UI thread (low-level hooks are dispatched on the
    // installing thread's message pump), so touching the UI is safe.
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _locked)
        {
            int msg = wParam.ToInt32();
            bool keyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

            if (keyDown)
            {
                // Restore if minimized ("it comes back if they try to type")
                // and blink twice to acknowledge the blocked key.
                if (InvokeRequired) BeginInvoke(OnBlockedKey);
                else OnBlockedKey();
            }

            // Returning 1 swallows the keystroke. The mouse is untouched
            // because we never installed a mouse hook.
            return (IntPtr)1;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // ---------------------------------------------------------------
    // Lock / unlock state
    // ---------------------------------------------------------------
    private void ToggleLock()
    {
        _locked = !_locked;
        if (_locked) EnterLocked();
        else EnterUnlocked();
    }

    private void EnterLocked()
    {
        SuspendLayout();
        ClientSize = LockedSize;
        StartPosition = FormStartPosition.CenterScreen;
        CenterToScreen();

        Text = "🔒 KEYBOARD LOCKED";
        TopMost = true;

        _status.Font = new Font("Segoe UI", 30f, FontStyle.Bold);
        _status.Text = "⚠  KEYBOARD LOCKED  ⚠\n\nClick below to unlock";
        _toggle.Text = "Unlock Keyboard";
        ResumeLayout();

        ApplyLockedStatic();
        BringToFront();
        Activate();
    }

    private void EnterUnlocked()
    {
        _flash.Stop();
        _flashTicks = 0;

        SuspendLayout();
        BackColor = Color.FromArgb(245, 245, 245);
        ClientSize = UnlockedSize;
        CenterToScreen();

        Text = "CatFoil";
        TopMost = false;

        _status.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        _status.ForeColor = Color.FromArgb(0, 130, 0);
        _status.Text = "Keyboard is ACTIVE";
        _toggle.Text = "Lock Keyboard";
        ResumeLayout();
    }

    private void RestoreWindow()
    {
        WindowState = FormWindowState.Normal;
        TopMost = true;
        Show();
        BringToFront();
        Activate();
    }

    private void OnBlockedKey()
    {
        if (WindowState == FormWindowState.Minimized)
            RestoreWindow();

        // Don't stack bursts: only start a fresh 2-blink once the last finished.
        if (!_flash.Enabled)
            StartKeyFlash();
    }

    private void StartKeyFlash()
    {
        // 2 flashes = ON, OFF, ON, OFF  (ends back on the static locked look)
        _flashTicks = 4;
        _flashOn = false;   // first tick flips this to ON
        _flash.Start();
    }

    private void FlashTick()
    {
        _flashOn = !_flashOn;
        if (_flashOn)
        {
            BackColor = Color.FromArgb(180, 0, 0);
            _status.ForeColor = Color.White;
        }
        else
        {
            ApplyLockedStatic();
        }

        if (--_flashTicks <= 0)
        {
            _flash.Stop();
            ApplyLockedStatic();   // settle on the static locked look
        }
    }

    private void ApplyLockedStatic()
    {
        BackColor = Color.FromArgb(255, 235, 235);
        _status.ForeColor = Color.FromArgb(180, 0, 0);
    }
}
