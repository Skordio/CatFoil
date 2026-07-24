using System;
using System.Windows.Forms;

namespace CatFoil;

/// <summary>
/// Shared state for the settings pages: the live <see cref="Settings"/> object
/// plus the immediate-apply pipeline.
///
/// There is no Save/Cancel — every edit takes effect the moment it is made, so
/// pages mutate through <see cref="Apply"/>. That notifies the app straight
/// away (so the change is live) but coalesces the disk writes, which keeps
/// dragging a spinner or a slider from writing settings.json on every tick.
/// </summary>
internal sealed class SettingsSession : IDisposable
{
    // Long enough that a dragged control writes once when it settles, short
    // enough that a crash just after an edit can't plausibly lose it.
    private const int SaveDebounceMs = 500;

    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = SaveDebounceMs };
    private bool _savePending;

    public Settings Settings { get; }

    /// <summary>Raised after any edit so the app can apply it live.</summary>
    public event Action? Changed;

    /// <summary>Raised once an elevated instance has been launched; the app
    /// should quit so that instance can take over the single-instance slot.</summary>
    public event Action? RestartElevatedRequested;

    /// <summary>True while the hotkey box is listening for a new binding. The
    /// app suspends the current hotkey meanwhile — otherwise pressing the very
    /// combo being rebound toggles the lock instead of reaching the box.</summary>
    public event Action<bool>? HotkeyCaptureChanged;

    public SettingsSession(Settings settings)
    {
        Settings = settings;
        _saveTimer.Tick += (_, _) => Flush();
    }

    /// <summary>Make an edit: applied live at once, written to disk shortly after.</summary>
    public void Apply(Action<Settings> edit)
    {
        edit(Settings);
        _savePending = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke();
    }

    /// <summary>Announce a change that the caller has already persisted itself.</summary>
    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>Write any pending edit now instead of waiting for the debounce.</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        if (!_savePending) return;
        _savePending = false;
        Settings.Save();
    }

    public void SetHotkeyCapture(bool capturing) => HotkeyCaptureChanged?.Invoke(capturing);

    public void RequestRestartElevated() => RestartElevatedRequested?.Invoke();

    public void Dispose() => _saveTimer.Dispose();
}
