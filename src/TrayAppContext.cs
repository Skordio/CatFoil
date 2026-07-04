using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using CatFoil.Licensing;
using Microsoft.Win32;

namespace CatFoil;

/// <summary>
/// The tray-first application shell: owns the tray icon, keyboard hook,
/// hotkey, overlay, trial timer, and the (lazily shown) main/settings windows.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private const int ToggleDebounceMs = 400;
    private const int TrialWarningSeconds = 120;
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CatFoil";

    private readonly Settings _settings;
    private readonly ILicenseProvider _license;
    private readonly KeyboardHook _hook = new();
    private readonly HotkeyManager _hotkey = new();
    private readonly MainForm _mainForm;
    private readonly OverlayForm _overlay;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _lockMenuItem;
    private readonly System.Windows.Forms.Timer _trialTimer = new() { Interval = 1000 };
    private readonly Icon _appIcon;

    private SettingsForm? _settingsForm;
    private RegisteredWaitHandle? _showWait;
    private int _trialSecondsLeft;
    private bool _trialWarningShown;
    private int _lastToggleTick;

    public TrayAppContext(EventWaitHandle showEvent)
    {
        _settings = Settings.Load();
        _license = new LemonSqueezyProvider(_settings);
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
        ApplyStartWithWindows();

        _trialTimer.Tick += (_, _) => TrialTick();

        _lockMenuItem = new ToolStripMenuItem("Lock Keyboard", null, (_, _) => ToggleLock());
        var openItem = new ToolStripMenuItem("Open CatFoil", null, (_, _) => ShowMainWindow());
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);

        var menu = new ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(_lockMenuItem);
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

    private void SetLocked(bool locked, bool expired = false)
    {
        if (locked == _hook.IsLocked) return;

        if (locked)
        {
            _hook.Lock();
            _mainForm.SetLockedUi(true);
            _overlay.SetActive(_settings.ShowOverlay);
            _tray.Text = "CatFoil — KEYBOARD LOCKED";
            _lockMenuItem.Text = "Unlock Keyboard";

            _trialWarningShown = false;
            if (!_license.IsLicensed)
            {
                _trialSecondsLeft = TrialDurationSeconds();
                _trialTimer.Start();
            }
        }
        else
        {
            _trialTimer.Stop();
            _hook.Unlock();
            _overlay.SetActive(false);
            _overlay.SetRemaining(null);
            _mainForm.SetLockedUi(false);
            _tray.Text = "CatFoil — keyboard active";
            _lockMenuItem.Text = "Lock Keyboard";

            if (expired)
            {
                _mainForm.ShowTrialExpired();
                ShowMainWindow();
            }
        }
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
    // Trial
    // ---------------------------------------------------------------
    private static int TrialDurationSeconds()
    {
        const int full = 30 * 60;

        // Dev override so the countdown can be tested without waiting 30 minutes.
        // It can only SHORTEN the session — otherwise setting one env var would
        // be a license bypass on release builds too.
        var env = Environment.GetEnvironmentVariable("CATFOIL_TRIAL_SECONDS");
        return int.TryParse(env, out int s) && s > 0 ? Math.Min(s, full) : full;
    }

    private void TrialTick()
    {
        _trialSecondsLeft--;
        if (_trialSecondsLeft <= 0)
        {
            SetLocked(false, expired: true);
            return;
        }

        if (_trialSecondsLeft <= TrialWarningSeconds)
        {
            var remaining = TimeSpan.FromSeconds(_trialSecondsLeft);
            if (!_trialWarningShown)
            {
                _trialWarningShown = true;
                ShowMainWindow();
            }
            _mainForm.ShowTrialCountdown(remaining);
            _overlay.SetRemaining(remaining);
        }
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

        _settingsForm = new SettingsForm(_settings, _license) { Icon = _appIcon };
        _settingsForm.SettingsSaved += () =>
        {
            ApplyHotkeySettings();
            ApplyStartWithWindows();
            _mainForm.RefreshHotkey();
            _overlay.ApplyAppearance(_settings.OverlayNormal, _settings.OverlayFullscreen);
            if (_hook.IsLocked)
                _overlay.SetActive(_settings.ShowOverlay);
        };
        _settingsForm.Show();
    }

    private void ApplyHotkeySettings()
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
            if (!_hotkey.Register(_settings.Hotkey))
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

    private void ApplyStartWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (_settings.StartWithWindows)
                key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Registry access denied — autostart just won't work; not fatal.
        }
    }

    // ---------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------
    private void ExitApp()
    {
        if (_hook.IsLocked) SetLocked(false);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
