using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
    private const int BlockedSoundThrottleMs = 700;

    private readonly Settings _settings;
    private readonly KeyboardHook _hook = new();
    private readonly HotkeyManager _hotkey = new();
    private readonly MainForm _mainForm;
    // One live badge per configured overlay, reconciled by SyncOverlays.
    private readonly List<OverlayForm> _overlayForms = new();
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

    // The settings UI, created on first visit and then alive for the app's
    // lifetime inside the main window (disposed with it). Per-visit semantics
    // are carried by SettingsShell.EndVisit, not by disposal.
    private SettingsShell? _shell;
    private RegisteredWaitHandle? _showWait;
    private int _lastToggleTick;
    private int _lockStartTick;
    private int _lastBlockedSoundTick;
    // True while the settings window is listening for a new hotkey binding.
    private bool _hotkeyCapture;
    // What the hotkey and startup registrations were last built from, so a live
    // edit that touches neither doesn't redo them. See ApplyLiveEdit.
    private (Keys, bool) _appliedHotkeyState;
    private (bool, bool) _appliedStartupState;

    public TrayAppContext(EventWaitHandle showEvent)
    {
        _settings = Settings.Load();
        _appIcon = LoadAppIcon();
        _lastToggleTick = unchecked(Environment.TickCount - ToggleDebounceMs);
        // Seed with a real tick value: left at 0, the throttle test would stay
        // false for the whole ~24.9-day window where TickCount is negative.
        _lastBlockedSoundTick = unchecked(Environment.TickCount - BlockedSoundThrottleMs);

        _mainForm = new MainForm(_settings) { Icon = _appIcon };
        _ = _mainForm.Handle;   // create the handle now so BeginInvoke works before the first Show
        _mainForm.ToggleRequested += ToggleLock;
        _mainForm.SettingsRequested += ShowSettings;

        SyncOverlays();

        // Hook events fire mid-hook; defer the real work so the hook returns fast.
        _hook.BlockedKeyPress += () => _mainForm.BeginInvoke(OnBlockedKey);
        _hook.UnlockComboPressed += () => _mainForm.BeginInvoke(() => SetLocked(false));
        // The chord engine is dormant (option pulled — see Settings) but the
        // wiring stays so re-adding it is one settings branch, not archaeology.
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
        // Installs from before 0.5.0 registered the elevated logon task with a
        // descriptor only Administrators can delete, so an unelevated uninstall
        // strands it. Repairing needs elevation — which that very task hands us
        // at logon — and spawns schtasks, so it stays off the startup path.
        System.Threading.Tasks.Task.Run(Startup.RepairTaskSecurity);
        // Seed the baselines ApplyLiveEdit compares against.
        _appliedHotkeyState = HotkeyState();
        _appliedStartupState = (_settings.StartWithWindows, _settings.StartElevatedOnBoot);

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
        menu.Items.Add(new ToolStripMenuItem("Statistics…", null, (_, _) => ShowStats()));
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

        // An elevation relaunch says which windows were open when the user hit
        // "Run as administrator"; re-open exactly those. StartMinimized only
        // governs an ordinary launch — following it here made the app vanish
        // mid-settings-visit for start-closed users.
        string[] launchArgs = Environment.GetCommandLineArgs();
        if (RestoreUi.Requested(launchArgs))
        {
            if (RestoreUi.MainWindow(launchArgs))
                ShowMainWindow();
            if (RestoreUi.SettingsPage(launchArgs) is string page)
            {
                ShowSettings();
                _shell!.SelectPage(page);
            }
        }
        else if (!_settings.StartMinimized)
        {
            ShowMainWindow();
        }

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
            _lockStartTick = Environment.TickCount;
            _settings.StatLockSessions++;
            _settings.Save();
            // The lock screen before the cue: SetLockedUi snaps out of the
            // settings view, and leaving settings ends the visit — whose
            // cleanup closes every MCI alias. Started first, a custom lock
            // cue would be silenced milliseconds in whenever the user locks
            // from inside settings.
            _mainForm.SetLockedUi(true);
            Sounds.Lock(_settings);
            ApplyOverlayActivation();
            _tray.Text = "CatFoil — KEYBOARD LOCKED";
            _lockMenuItem.Text = "Unlock Keyboard";
        }
        else
        {
            _timedTimer.Stop();
            _timedSecondsLeft = 0;
            _hook.Unlock();
            FlushLockStats();
            Sounds.Unlock(_settings);
            ApplyOverlayActivation();   // the hook is already unlocked, so all badges go away
            SetOverlayRemaining(null);
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
        SetOverlayRemaining(remaining);
    }

    private void OnBlockedKey()
    {
        if (!_hook.IsLocked) return;

        // Count it — this is a key the cat pressed that went nowhere. Kept in
        // memory and persisted at the next watchdog checkpoint or unlock, so a
        // key-mashing cat never causes a disk write per keystroke.
        _settings.StatBlockedKeys++;

        // The window is the unlock failsafe: bring it back if the user has no
        // visible way into CatFoil (or it's sitting minimized). With several
        // overlays configured, any one of them still on screen is a way in.
        if ((!_mainForm.Visible && !_overlayForms.Any(f => f.Visible))
            || _mainForm.WindowState == FormWindowState.Minimized)
        {
            ShowMainWindow();
        }

        foreach (OverlayForm form in _overlayForms) form.FlashBlockedKey();

        // Throttle the blocked-key sound so a held key doesn't machine-gun it.
        if (_settings.BlockedSound.Enabled)
        {
            int now = Environment.TickCount;
            if (unchecked(now - _lastBlockedSoundTick) >= BlockedSoundThrottleMs)
            {
                _lastBlockedSoundTick = now;
                Sounds.Blocked(_settings);
            }
        }
    }

    // ---------------------------------------------------------------
    // Overlays
    // ---------------------------------------------------------------

    /// <summary>
    /// Brings the live badges in line with the configured overlays: closes the
    /// ones whose item is gone, creates the ones that are new, and pushes the
    /// current appearance to the rest. Safe to call any time settings change —
    /// it is how an edit reaches the screen.
    /// </summary>
    private void SyncOverlays()
    {
        List<OverlayItem> items = _settings.EnsureOverlays();

        for (int i = _overlayForms.Count - 1; i >= 0; i--)
        {
            OverlayForm form = _overlayForms[i];
            if (items.Any(item => item.Id == form.OverlayId)) continue;
            _overlayForms.RemoveAt(i);
            form.SetActive(false);
            form.Close();
            form.Dispose();
        }

        for (int i = 0; i < items.Count; i++)
        {
            OverlayItem item = items[i];
            OverlayForm? form = _overlayForms.FirstOrDefault(f => f.OverlayId == item.Id);
            bool created = form is null;
            form ??= CreateOverlay(item);

            form.ApplyAppearance(item.Appearance, item.ShowIn);
            // Positioned only after the appearance is on, and only for a badge
            // that is new: automatic placement centres on the badge's size, and
            // before ApplyAppearance that is still the constructor's default 64
            // rather than this item's.
            if (created) form.ApplySavedPosition(item.Position, cascadeIndex: i);
        }

        ApplyOverlayActivation();
    }

    private OverlayForm CreateOverlay(OverlayItem item)
    {
        var form = new OverlayForm(_appIcon, item.Id);
        form.OpenRequested += ShowMainWindow;
        // Look the item up again on each drag rather than capturing it: the
        // settings object outlives any particular OverlayItem instance.
        form.PositionChanged += p => OnOverlayMoved(item.Id, p);
        _overlayForms.Add(form);
        return form;
    }

    private void OnOverlayMoved(string overlayId, Point position)
    {
        OverlayItem? item = _settings.Overlays.FirstOrDefault(o => o.Id == overlayId);
        if (item is null) return;
        item.Position = position;
        _settings.Save();
    }

    // A badge is on screen only while the keyboard is locked, the master
    // "show overlay" switch is on, and that particular overlay is enabled.
    private void ApplyOverlayActivation()
    {
        bool show = _hook.IsLocked && _settings.ShowOverlay;
        foreach (OverlayForm form in _overlayForms)
        {
            OverlayItem? item = _settings.Overlays.FirstOrDefault(o => o.Id == form.OverlayId);
            form.SetActive(show && item is { Enabled: true });
        }
    }

    private void SetOverlayRemaining(TimeSpan? remaining)
    {
        foreach (OverlayForm form in _overlayForms) form.SetRemaining(remaining);
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

    /// <summary>
    /// Credits the running lock session's elapsed time to the stats and
    /// persists them. Advances the baseline by exactly the whole seconds
    /// credited, so repeated flushes never drop the sub-second remainder.
    /// </summary>
    private void FlushLockStats()
    {
        long secs = unchecked((uint)Environment.TickCount - (uint)_lockStartTick) / 1000;
        _settings.StatLockedSeconds += secs;
        _lockStartTick = unchecked(_lockStartTick + (int)(secs * 1000));
        _settings.Save();
    }

    private long InProgressLockSeconds() => _hook.IsLocked
        ? unchecked((uint)Environment.TickCount - (uint)_lockStartTick) / 1000
        : 0L;

    // A reset during a lock session restarts that session's clock and counts
    // the in-progress session as 1, so the display never shows "0 sessions"
    // next to a nonzero (and growing) locked time.
    private void OnStatsReset()
    {
        if (!_hook.IsLocked) return;
        _lockStartTick = Environment.TickCount;
        _settings.StatLockSessions = 1;
    }

    private void ShowStats()
    {
        ShowSettings();
        _shell!.SelectPage<StatisticsPage>();
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
        if (_shell is null)
        {
            // Wired once, here — EnterSettings re-shows the same shell on every
            // later visit, so subscribing there would stack handlers.
            _shell = new SettingsShell(_settings, InProgressLockSeconds, OnStatsReset);
            _shell.SettingsSaved += ApplyLiveEdit;
            // The elevated relaunch is already running; quit so it can take over.
            _shell.RestartElevatedRequested += ExitApp;
            _shell.HotkeyCaptureChanged += OnHotkeyCaptureChanged;
        }

        ShowMainWindow();
        _mainForm.EnterSettings(_shell);
    }

    /// <summary>
    /// Applies a settings edit to the running app.
    ///
    /// Immediate-apply raises this on <em>every</em> edit, which since the
    /// overlay and sound editors arrived means every tick of a slider drag and
    /// every keystroke in a name box. Re-registering the hotkey and rewriting
    /// the HKCU Run key at that rate is pure waste — and when the hotkey is
    /// already owned by another app, it would put a "could not register" balloon
    /// on screen per tick. So those two only run when something they actually
    /// depend on has changed; the rest is cheap enough to run every time.
    /// </summary>
    private void ApplyLiveEdit()
    {
        var hotkeyState = HotkeyState();
        if (hotkeyState != _appliedHotkeyState)
        {
            _appliedHotkeyState = hotkeyState;
            ApplyHotkeySettings();
            _mainForm.RefreshHotkey();
        }

        var startupState = (_settings.StartWithWindows, _settings.StartElevatedOnBoot);
        if (startupState != _appliedStartupState)
        {
            _appliedStartupState = startupState;
            ApplyStartupSettings();
        }

        SyncOverlays();
        ApplyIdleTimer();
    }

    // Everything ApplyHotkeySettings reads, as a comparable value.
    private (Keys, bool) HotkeyState() => (_settings.Hotkey, _settings.HotkeyEnabled);

    /// <summary>
    /// Stop and resume listening for the hotkey while the settings window binds
    /// a new one. Settings applies edits immediately, so without this the combo
    /// the user just bound is live again on the next keystroke — and pressing it
    /// to try another binding would toggle the lock instead of reaching the box.
    /// </summary>
    private void OnHotkeyCaptureChanged(bool capturing)
    {
        _hotkeyCapture = capturing;
        ApplyHotkeySettings(announceFailure: false);
    }

    private void ApplyHotkeySettings(bool announceFailure = true)
    {
        // Binding a new hotkey: nothing should be listening. Guarding here
        // rather than at the call sites covers the watchdog and the settings
        // window's live-apply, which both land in here every few seconds.
        if (_hotkeyCapture)
        {
            _hotkey.Unregister();
            return;
        }

        // No chord branch here anymore: the option is pulled (see Settings),
        // so the hook's chord engine is never armed and RegisterHotKey is
        // always the lock trigger.
        _hook.UnlockCombo = _settings.HotkeyEnabled ? _settings.Hotkey : Keys.None;
        if (_settings.HotkeyEnabled)
        {
            if (!_hotkey.Register(_settings.Hotkey) && announceFailure)
            {
                _tray?.ShowBalloonTip(3000, "CatFoil",
                    "Could not register the hotkey " + HotkeyText.Format(_settings.Hotkey) +
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
    /// Re-arms the hotkey and the keyboard hook. Windows quietly drops both
    /// after long idle or a sleep/resume: a low-level hook that missed
    /// LowLevelHooksTimeout is removed with no signal to us, and RegisterHotKey
    /// bindings can be lost across power transitions. Called on a watchdog timer
    /// and on power-resume / session-unlock so both keep working without the
    /// user having to reopen a window.
    /// </summary>
    private void ReassertInput()
    {
        // Reinstall unconditionally — including while locked. Reinstall adds
        // the new hook before dropping the old one, and hook callbacks only
        // run when this thread pumps messages, so there is no instant during
        // the swap where a keystroke can slip past the lock. The old behavior
        // (skip while locked, on the theory the hook is "warm") left the one
        // state where a silently-dropped hook actually leaks keys unrepaired
        // for the whole session; this bounds that exposure to one tick.
        _hook.Reinstall(out _);

        // Drop tracked modifier/chord state Windows says is stale — key-UPs
        // missed on the secure desktop (Ctrl+Alt+Del, UAC, Win+L) or during a
        // dropped-hook window otherwise jam the unlock combo permanently.
        _hook.ResyncModifiers();

        if (_hook.IsLocked)
            // Checkpoint the running session's stats each tick so a crash or
            // force-kill while locked loses at most a minute, not the session.
            FlushLockStats();

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
        // MCI handles are process-wide and hold the audio files open.
        AudioPlayer.CloseAll();
        _mainForm.AllowClose = true;
        foreach (OverlayForm form in _overlayForms) form.Close();
        _overlayForms.Clear();
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
