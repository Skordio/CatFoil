using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CatFoil;

/// <summary>
/// The tray-first application shell: owns the tray icon, keyboard hook,
/// hotkey, overlay, and the (lazily shown) main/settings windows.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private const int ToggleDebounceMs = 400;

    private readonly Settings _settings;
    private readonly KeyboardHook _hook = new();
    private readonly HotkeyManager _hotkey = new();
    private readonly MainForm _mainForm;
    private readonly OverlayForm _overlay;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _lockMenuItem;
    // Periodically re-arms the hotkey and hook: Windows quietly drops both after
    // long idle / sleep, and otherwise nothing restores them until the user
    // reopens a window. 60s keeps the vulnerable gap short without churn.
    private readonly System.Windows.Forms.Timer _inputWatchdog = new() { Interval = 60_000 };
    // Polls system idle time to auto-lock after inactivity.
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 5000 };
    // Counts down a user-chosen timed lock, then auto-unlocks.
    private readonly System.Windows.Forms.Timer _timedTimer = new() { Interval = 1000 };
    private int _timedSecondsLeft;
    private readonly Icon _appIcon;

    private SettingsForm? _settingsForm;
    private RegisteredWaitHandle? _showWait;
    private int _lastToggleTick;

    public TrayAppContext(EventWaitHandle showEvent)
    {
        _settings = Settings.Load();
        _appIcon = LoadAppIcon();
        _lastToggleTick = unchecked(Environment.TickCount - ToggleDebounceMs);

        _mainForm = new MainForm(_settings) { Icon = _appIcon };
        _ = _mainForm.Handle;   // create the handle now so BeginInvoke works before the first Show
        _mainForm.ToggleRequested += ToggleLock;
        _mainForm.SettingsRequested += ShowSettings;
        _mainForm.ExitRequested += ExitApp;

        _overlay = new OverlayForm(_appIcon);
        _overlay.ApplyAppearance(_settings.OverlayNormal, _settings.OverlayFullscreen);
        _overlay.ApplySavedPosition(_settings.OverlayPosition);
        _overlay.OpenRequested += ShowMainWindow;
        _overlay.PositionChanged += p =>
        {
            _settings.OverlayPosition = p;
            _settings.Save();
        };

        // Hook events fire mid-hook; defer the real work so the hook returns fast.
        _hook.BlockedKeyPress += () => _mainForm.BeginInvoke(OnBlockedKey);
        _hook.UnlockComboPressed += () => _mainForm.BeginInvoke(() => SetLocked(false));
        _hook.ChordPressed += () => _mainForm.BeginInvoke(ToggleLock);
        if (!_hook.Install(out int hookError))
        {
            MessageBox.Show(
                "Failed to install the keyboard hook (error " + hookError + ").",
                "CatFoil", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        _hotkey.HotkeyPressed += ToggleLock;
        ApplyHotkeySettings();
        ApplyStartupSettings();

        _timedTimer.Tick += (_, _) => TimedTick();

        // Keep global input alive across long idle and sleep (see ReassertInput).
        _inputWatchdog.Tick += (_, _) => ReassertInput();
        _inputWatchdog.Start();
        // The idle poll only matters for auto-lock; leave it stopped otherwise so
        // the process isn't woken every 5s to do nothing. ApplyIdleTimer keeps it
        // in sync with the setting here and after every save.
        _idleTimer.Tick += (_, _) => IdleCheck();
        ApplyIdleTimer();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _lockMenuItem = new ToolStripMenuItem("Lock Keyboard", null, (_, _) => ToggleLock());
        var openItem = new ToolStripMenuItem("Open CatFoil", null, (_, _) => ShowMainWindow());
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);

        var lockForItem = new ToolStripMenuItem("Lock for…");
        foreach (int minutes in new[] { 5, 15, 30, 60 })
        {
            int m = minutes;   // capture
            lockForItem.DropDownItems.Add(new ToolStripMenuItem($"{m} minutes", null, (_, _) => LockFor(m)));
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(_lockMenuItem);
        menu.Items.Add(lockForItem);
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "CatFoil — keyboard active",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        if (!_settings.StartMinimized)
            ShowMainWindow();

        if (!_settings.WelcomeShown)
        {
            ShowMainWindow();
            // Defer until the message loop is running so the dialog centers
            // on an already-visible main window.
            _mainForm.BeginInvoke(ShowWelcome);
        }

        // A second launched instance sets this event to say "show yourself".
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            showEvent, (_, _) => _mainForm.BeginInvoke(ShowMainWindow), null, -1, false);
    }

    // ---------------------------------------------------------------
    // Lock / unlock
    // ---------------------------------------------------------------
    private void ToggleLock()
    {
        // Debounce: the unlock combo and the lock hotkey are the same keys, so
        // a held combo could otherwise re-toggle every key-repeat.
        int now = Environment.TickCount;
        if (unchecked(now - _lastToggleTick) < ToggleDebounceMs) return;
        _lastToggleTick = now;

        SetLocked(!_hook.IsLocked);
    }

    private void SetLocked(bool locked)
    {
        if (locked == _hook.IsLocked) return;

        if (locked)
        {
            _hook.Lock();
            _mainForm.SetLockedUi(true);
            _overlay.SetActive(_settings.ShowOverlay);
            _tray.Text = "CatFoil — KEYBOARD LOCKED";
            _lockMenuItem.Text = "Unlock Keyboard";
        }
        else
        {
            _timedTimer.Stop();
            _timedSecondsLeft = 0;
            _hook.Unlock();
            _overlay.SetActive(false);
            _overlay.SetRemaining(null);
            _mainForm.SetLockedUi(false);
            _tray.Text = "CatFoil — keyboard active";
            _lockMenuItem.Text = "Lock Keyboard";
        }
    }

    // Auto-lock once the machine has had no keyboard/mouse input for the
    // configured stretch. Mouse activity resets the idle clock, so simply
    // stepping away (no input at all) triggers it; using the mouse does not.
    private void IdleCheck()
    {
        if (!_settings.AutoLockEnabled || _hook.IsLocked) return;

        // Don't lock during passive full-screen use (movies, video calls,
        // full-screen slideshows, games): those legitimately produce no
        // keyboard/mouse input for long stretches, so idle time alone would
        // wrongly read them as "away from the desk" and lock mid-activity.
        if (OverlayForm.ForegroundIsFullscreen()) return;

        // Don't lock while the user is reading/configuring one of CatFoil's own
        // windows (Settings, Welcome, main): they may sit still on it past the
        // threshold, and locking mid-configuration is confusing.
        if (OverlayForm.ForegroundIsOwnProcess()) return;

        uint threshold = (uint)Math.Clamp(_settings.AutoLockMinutes, 1, 120) * 60_000u;
        if (IdleTime.Milliseconds() >= threshold)
            SetLocked(true);
    }

    // Run the 5s idle poll only while auto-lock is on.
    private void ApplyIdleTimer()
    {
        if (_settings.AutoLockEnabled) _idleTimer.Start();
        else _idleTimer.Stop();
    }

    // ---------------------------------------------------------------
    // Timed lock ("Lock for N minutes", then auto-unlock)
    // ---------------------------------------------------------------
    private void LockFor(int minutes)
    {
        if (!_hook.IsLocked) SetLocked(true);

        _timedSecondsLeft = Math.Max(1, minutes) * 60;
        _timedTimer.Start();
        UpdateTimedCountdown();
    }

    private void TimedTick()
    {
        _timedSecondsLeft--;
        if (_timedSecondsLeft <= 0)
        {
            SetLocked(false);
            return;
        }
        UpdateTimedCountdown();
    }

    private void UpdateTimedCountdown()
    {
        var remaining = TimeSpan.FromSeconds(_timedSecondsLeft);
        _mainForm.ShowLockCountdown(remaining);
        _overlay.SetRemaining(remaining);
    }

    private void OnBlockedKey()
    {
        if (!_hook.IsLocked) return;

        // The window is the unlock failsafe: bring it back if the user has no
        // visible way into CatFoil (or it's sitting minimized).
        if ((!_mainForm.Visible && !_overlay.Visible) || _mainForm.WindowState == FormWindowState.Minimized)
            ShowMainWindow();

        _overlay.FlashBlockedKey();
    }

    // ---------------------------------------------------------------
    // Windows & settings
    // ---------------------------------------------------------------
    private void ShowMainWindow()
    {
        _mainForm.Show();
        if (_mainForm.WindowState == FormWindowState.Minimized)
            _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.BringToFront();
        _mainForm.Activate();
    }

    private void ShowWelcome()
    {
        using (var welcome = new WelcomeForm(_settings))
            welcome.ShowDialog(_mainForm);

        _settings.WelcomeShown = true;
        _settings.Save();
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settings) { Icon = _appIcon };
        _settingsForm.SettingsSaved += () =>
        {
            ApplyHotkeySettings();
            ApplyStartupSettings();
            _mainForm.RefreshHotkey();
            _overlay.ApplyAppearance(_settings.OverlayNormal, _settings.OverlayFullscreen);
            if (_hook.IsLocked)
                _overlay.SetActive(_settings.ShowOverlay);
            ApplyIdleTimer();
        };
        // The elevated relaunch is already running; quit so it can take over.
        _settingsForm.RestartElevatedRequested += ExitApp;
        _settingsForm.Show();
    }

    private void ApplyHotkeySettings(bool announceFailure = true)
    {
        // Chord mode: our hook detects the combo in both lock states and
        // RegisterHotKey is retired (it can't express multi-key chords).
        if (_settings.HotkeyEnabled && _settings.UseChordHotkey && _settings.ChordKeys.Length >= 2)
        {
            _hotkey.Unregister();
            _hook.UnlockCombo = Keys.None;
            _hook.ChordModifiers = _settings.ChordModifiers;
            _hook.SetChordKeys(_settings.ChordKeys);
            return;
        }

        _hook.SetChordKeys(Array.Empty<Keys>());
        _hook.UnlockCombo = _settings.HotkeyEnabled ? _settings.Hotkey : Keys.None;
        if (_settings.HotkeyEnabled)
        {
            if (!_hotkey.Register(_settings.Hotkey) && announceFailure)
            {
                _tray?.ShowBalloonTip(3000, "CatFoil",
                    "Could not register the hotkey " + SettingsForm.FormatHotkey(_settings.Hotkey) +
                    " — another app may already be using it.", ToolTipIcon.Warning);
            }
        }
        else
        {
            _hotkey.Unregister();
        }
    }

    // ---------------------------------------------------------------
    // Keep global input alive across idle / sleep
    // ---------------------------------------------------------------

    /// <summary>
    /// Re-arms the hotkey and (while unlocked) the keyboard hook. Windows quietly
    /// drops both after long idle or a sleep/resume: a low-level hook that missed
    /// LowLevelHooksTimeout is removed with no signal to us, and RegisterHotKey
    /// bindings can be lost across power transitions. Called on a watchdog timer
    /// and on power-resume / session-unlock so the hotkey keeps working without
    /// the user having to reopen a window.
    /// </summary>
    private void ReassertInput()
    {
        // Only re-add the hook while unlocked: locked, it's firing on every key
        // (so it's warm, not idle-dead), and reinstalling would open a brief gap
        // where a keystroke could leak past the lock.
        if (!_hook.IsLocked)
            _hook.Reinstall(out _);

        // Re-register quietly — a genuine conflict was already reported at startup,
        // and we don't want a balloon every 60 seconds.
        ApplyHotkeySettings(announceFailure: false);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            MarshalReassert();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)
            MarshalReassert();
    }

    // These events fire on the SystemEvents thread, so hop to the UI thread to
    // re-arm. During shutdown one can arrive after the form's handle is gone,
    // where BeginInvoke throws unhandled on that thread — guard and swallow it.
    private void MarshalReassert()
    {
        if (!_mainForm.IsHandleCreated || _mainForm.IsDisposed) return;
        try { _mainForm.BeginInvoke(ReassertInput); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private void ApplyStartupSettings() => Startup.Apply(_settings);

    // ---------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------
    private void ExitApp()
    {
        if (_hook.IsLocked) SetLocked(false);
        DetachInputWatchdog();
        _showWait?.Unregister(null);
        _showWait = null;
        _tray.Visible = false;
        _hotkey.Dispose();
        _hook.Dispose();
        _mainForm.AllowClose = true;
        _overlay.Close();
        _mainForm.Close();
        ExitThread();
    }

    // SystemEvents keeps a strong reference to its handlers, so leaving them
    // subscribed would keep this context alive; always detach on teardown.
    private void DetachInputWatchdog()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _inputWatchdog.Stop();
        _idleTimer.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachInputWatchdog();
            _inputWatchdog.Dispose();
            _idleTimer.Dispose();
            _timedTimer.Dispose();
            _showWait?.Unregister(null);
            _tray?.Dispose();
            _hotkey.Dispose();
            _hook.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            using var stream = typeof(TrayAppContext).Assembly
                .GetManifestResourceStream("CatFoil.assets.cat.ico");
            if (stream != null) return new Icon(stream);
        }
        catch { }

        try
        {
            var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (ico != null) return ico;
        }
        catch { }

        return SystemIcons.Application;
    }
}
