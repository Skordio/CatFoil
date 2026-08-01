using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MouseDeviceBlockPrototype;

// Prototype for a possible CatFoil mode: block input from ONE pointing device
// (e.g. the laptop trackpad) while others keep working.
//
// Why two APIs at once: a low-level mouse hook (WH_MOUSE_LL) is the only
// user-mode way to BLOCK mouse input, but Windows merges every mouse into one
// stream before the hook sees it, so the hook cannot tell devices apart. Raw
// Input (WM_INPUT) is the only way to tell devices apart, but it cannot block.
// The combination works because of a documented quirk: eating an event in the
// hook does NOT stop its WM_INPUT from arriving — Raw Input always sees
// everything, blocked or not.
//
// The open question this prototype answers empirically (monitor mode): does the
// WM_INPUT attribution arrive BEFORE the hook has to decide about the same
// event? If yes, per-event attribution is exact. If no, the sliding-window
// heuristic below still blocks the device's streams, at the cost of the first
// event of a burst leaking through and (only during genuinely simultaneous use
// of two mice) a few events of the other device being eaten too.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--list") >= 0)
        {
            foreach (MouseDevice d in RawMouse.Enumerate())
                Console.WriteLine($"0x{d.Handle:X}  {d.Name}\n        {d.Path}");
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

internal sealed record MouseDevice(IntPtr Handle, string Path, string Name);

// Raw Input enumeration + device naming.
internal static class RawMouse
{
    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIDI_DEVICENAME = 0x20000007;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr Device;
        public uint Type;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(
        [Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoW(
        IntPtr device, uint command, char[]? data, ref uint size);

    public static List<MouseDevice> Enumerate()
    {
        var result = new List<MouseDevice>();
        uint count = 0;
        uint cb = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, cb) == unchecked((uint)-1) || count == 0)
            return result;

        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, cb) == unchecked((uint)-1))
            return result;

        foreach (RAWINPUTDEVICELIST entry in list)
        {
            if (entry.Type != RIM_TYPEMOUSE) continue;
            string path = DevicePath(entry.Device);
            result.Add(new MouseDevice(entry.Device, path, FriendlyName(path)));
        }
        return result;
    }

    private static string DevicePath(IntPtr device)
    {
        uint size = 0;
        GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, null, ref size);
        if (size == 0) return "";
        var buf = new char[size + 1];
        uint written = GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, buf, ref size);
        return written == unchecked((uint)-1) ? "" : new string(buf, 0, (int)written).TrimEnd('\0');
    }

    // "\\?\HID#VID_046D&PID_C52B#8&2f...&0&0000#{guid}" is also the device
    // instance path with '#' for '\', so the human-readable name is sitting in
    // the registry under Enum. DeviceDesc is "@oem1.inf,%foo%;HID-compliant
    // mouse" — the display text is after the last semicolon.
    private static string FriendlyName(string devicePath)
    {
        try
        {
            string trimmed = devicePath.TrimStart('\\', '?', '.');
            string[] parts = trimmed.Split('#');
            if (parts.Length >= 3)
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}");
                if (key?.GetValue("DeviceDesc") is string desc)
                {
                    int i = desc.LastIndexOf(';');
                    return i >= 0 ? desc[(i + 1)..] : desc;
                }
            }
        }
        catch
        {
            // No name is fine — fall through to the raw path.
        }
        return devicePath.Length == 0 ? "(unnamed device)" : devicePath;
    }
}

internal sealed class MainForm : Form
{
    // --- Raw Input ---
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    // RAWMOUSE with the union flattened; the 2-byte pad matches x64 alignment.
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort Flags;
        private ushort _pad;
        public uint Buttons;        // low 16 bits = usButtonFlags
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        RAWINPUTDEVICE[] devices, uint count, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput, uint command, IntPtr data, ref uint size, uint cbSizeHeader);

    // --- Hotkey ---
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1;
    private const uint MOD_SHIFT = 0x4;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    // --- state ---
    private const int EatWindowMs = 100;     // sliding attribution window
    private const int FailsafeSeconds = 120; // blocking always self-terminates

    private readonly ListView _devices = new();
    private readonly Button _refresh = new();
    private readonly CheckBox _monitor = new();
    private readonly CheckBox _verbose = new();
    private readonly Button _block = new();
    private readonly Button _stop = new();
    private readonly ListBox _log = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    private MouseHook? _hook;
    private bool _blocking;
    private IntPtr _target;
    private string _targetName = "";
    private long _eatUntil;                  // TickCount64 the hook eats until
    private long _eaten, _passedInjected;
    private int _secondsLeft;
    private string _unlockText = "";

    // Ordering proof: a single sequence number bumped by BOTH streams. If the
    // raw line for an event carries a lower number than the hook line, raw
    // arrived first and exact per-event attribution would be possible.
    private long _seq;
    private long _rawSeen, _hookSeen;

    public MainForm()
    {
        Text = "Mouse device block — CatFoil prototype";
        ClientSize = new System.Drawing.Size(860, 560);
        MinimumSize = new System.Drawing.Size(700, 480);

        _devices.SetBounds(12, 12, 836, 150);
        _devices.View = View.Details;
        _devices.FullRowSelect = true;
        _devices.MultiSelect = false;
        _devices.HideSelection = false;
        _devices.Columns.Add("Device", 280);
        _devices.Columns.Add("Handle", 110);
        _devices.Columns.Add("Path", 430);
        _devices.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        _refresh.SetBounds(12, 172, 90, 30);
        _refresh.Text = "Refresh";
        _refresh.Click += (_, _) => LoadDevices();

        _monitor.SetBounds(120, 176, 160, 24);
        _monitor.Text = "Monitor attribution";
        _monitor.CheckedChanged += (_, _) => _verbose.Enabled = _monitor.Checked;

        _verbose.SetBounds(286, 176, 150, 24);
        _verbose.Text = "Verbose (every event)";
        _verbose.Enabled = false;

        _block.SetBounds(500, 172, 190, 30);
        _block.Text = "Block selected device";
        _block.Click += (_, _) => StartBlocking();

        _stop.SetBounds(700, 172, 148, 30);
        _stop.Text = "Stop (Alt+G)";
        _stop.Enabled = false;
        _stop.Click += (_, _) => StopBlocking("stop button");
        _stop.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _log.SetBounds(12, 212, 836, 300);
        _log.IntegralHeight = false;
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _log.Font = new System.Drawing.Font("Consolas", 9f);

        _status.SetBounds(12, 522, 836, 26);
        _status.Text = "Idle. Select a device, then Block. Blocking always auto-stops after "
                     + FailsafeSeconds + " s.";
        _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        Controls.AddRange(new Control[] { _devices, _refresh, _monitor, _verbose, _block, _stop, _log, _status });

        _tick.Tick += (_, _) => OnSecond();
        LoadDevices();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Generic mouse (usage page 1, usage 2). INPUTSINK = deliver WM_INPUT
        // even while we're in the background — blocking must work everywhere.
        var reg = new[]
        {
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_INPUTSINK, Target = Handle },
        };
        if (!RegisterRawInputDevices(reg, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log("!! RegisterRawInputDevices failed — attribution unavailable");
    }

    private void LoadDevices()
    {
        _devices.Items.Clear();
        foreach (MouseDevice d in RawMouse.Enumerate())
        {
            var item = new ListViewItem(new[] { d.Name, $"0x{d.Handle:X}", d.Path }) { Tag = d };
            _devices.Items.Add(item);
        }
        Log($"{_devices.Items.Count} mouse device(s) found");
    }

    // ---------------------------------------------------------------
    // Blocking
    // ---------------------------------------------------------------

    private void StartBlocking()
    {
        if (_blocking) return;
        if (_devices.SelectedItems.Count != 1 || _devices.SelectedItems[0].Tag is not MouseDevice dev)
        {
            MessageBox.Show(this, "Select the device to block first.", Text);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Block all input from:\n\n    {dev.Name}\n\n" +
            $"Ways out:\n" +
            $"  \u2022 Alt+G (or Alt+Shift+G if Alt+G is taken — the banner will say)\n" +
            $"  \u2022 any other mouse still works — click Stop\n" +
            $"  \u2022 automatic stop after {FailsafeSeconds} seconds, no matter what\n" +
            $"  \u2022 Ctrl+Alt+Del always works\n\nContinue?",
            "Block this device?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        // CatFoil itself may own Alt+G; fall back rather than fail.
        if (RegisterHotKey(Handle, 1, MOD_ALT, (uint)Keys.G))
            _unlockText = "Alt+G";
        else if (RegisterHotKey(Handle, 1, MOD_ALT | MOD_SHIFT, (uint)Keys.G))
            _unlockText = "Alt+Shift+G  (Alt+G is registered by another app — CatFoil?)";
        else
        {
            MessageBox.Show(this, "Could not register an unlock hotkey — not blocking.", Text);
            return;
        }

        _hook = new MouseHook(ShouldEat);
        if (!_hook.Install())
        {
            _hook.Dispose();
            _hook = null;
            UnregisterHotKey(Handle, 1);
            MessageBox.Show(this, "Mouse hook failed to install — not blocking.", Text);
            return;
        }

        _target = dev.Handle;
        _targetName = dev.Name;
        _eatUntil = 0;
        _eaten = 0;
        _passedInjected = 0;
        _blocking = true;
        _secondsLeft = FailsafeSeconds;
        _stop.Enabled = true;
        _block.Enabled = false;
        _tick.Start();
        Log($"BLOCKING {dev.Name} — unlock: {_unlockText}, auto-stop {FailsafeSeconds}s");
        UpdateStatus();
    }

    private void StopBlocking(string how)
    {
        if (!_blocking) return;
        _blocking = false;
        _tick.Stop();
        _hook?.Dispose();
        _hook = null;
        UnregisterHotKey(Handle, 1);
        _stop.Enabled = false;
        _block.Enabled = true;
        Log($"UNBLOCKED via {how} — ate {_eaten} event(s), passed {_passedInjected} injected");
        _status.Text = $"Idle. Last block ate {_eaten} event(s).";
    }

    // Called by the hook for every real mouse event while installed.
    private bool ShouldEat(uint msg, MouseHook.MSLLHOOKSTRUCT data)
    {
        _hookSeen++;
        long n = ++_seq;

        // Injected input (SendInput etc.) has no physical device and must keep
        // working: remote assistance and accessibility tools inject.
        const uint LLMHF_INJECTED = 1;
        if ((data.Flags & LLMHF_INJECTED) != 0)
        {
            _passedInjected++;
            return false;
        }

        bool eat = _blocking && Environment.TickCount64 <= _eatUntil;
        if (eat) _eaten++;

        if (_monitor.Checked && (_verbose.Checked || msg != 0x0200 /* WM_MOUSEMOVE */))
            Log($"H#{n} {MsgName(msg)} ({data.Pt.X},{data.Pt.Y}) -> {(eat ? "EAT" : "pass")}");
        return eat;
    }

    // ---------------------------------------------------------------
    // WM_INPUT: attribute the event to a device
    // ---------------------------------------------------------------

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == 1)
        {
            StopBlocking("hotkey");
            return;
        }
        if (m.Msg == WM_INPUT)
        {
            OnRawInput(m.LParam);
            // fall through to DefWindowProc for cleanup, per the docs
        }
        base.WndProc(ref m);
    }

    private void OnRawInput(IntPtr rawHandle)
    {
        uint size = 0;
        uint cbHeader = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(rawHandle, RID_INPUT, IntPtr.Zero, ref size, cbHeader);
        if (size == 0) return;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawHandle, RID_INPUT, buf, ref size, cbHeader) != size) return;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (header.Type != 0 /* RIM_TYPEMOUSE */) return;
            var mouse = Marshal.PtrToStructure<RAWMOUSE>(buf + (int)cbHeader);

            _rawSeen++;
            long n = ++_seq;

            bool fromTarget = _blocking && header.Device == _target;
            if (fromTarget)
            {
                // Slide the window forward: as long as the blocked device keeps
                // producing events, the hook keeps eating.
                _eatUntil = Environment.TickCount64 + EatWindowMs;
            }

            if (_monitor.Checked)
            {
                bool isMove = mouse.Buttons == 0 && (mouse.LastX != 0 || mouse.LastY != 0);
                if (_verbose.Checked || !isMove)
                    Log($"R#{n} dev=0x{header.Device:X} dx={mouse.LastX} dy={mouse.LastY} " +
                        $"btn=0x{mouse.Buttons & 0xFFFF:X}{(fromTarget ? "  [BLOCKED DEVICE]" : "")}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ---------------------------------------------------------------

    private void OnSecond()
    {
        if (--_secondsLeft <= 0)
        {
            StopBlocking("failsafe timeout");
            return;
        }
        UpdateStatus();
    }

    private void UpdateStatus() =>
        _status.Text = $"BLOCKING {_targetName} — unlock: {_unlockText} — " +
                       $"auto-stop in {_secondsLeft}s — eaten {_eaten} — " +
                       $"raw {_rawSeen} / hook {_hookSeen}";

    private static string MsgName(uint msg) => msg switch
    {
        0x0200 => "move",
        0x0201 => "ldown",
        0x0202 => "lup",
        0x0204 => "rdown",
        0x0205 => "rup",
        0x0207 => "mdown",
        0x0208 => "mup",
        0x020A => "wheel",
        0x020E => "hwheel",
        _ => $"0x{msg:X}",
    };

    private void Log(string line)
    {
        Console.WriteLine(line);
        if (_log.Items.Count > 2000) _log.Items.RemoveAt(0);
        _log.Items.Add(line);
        _log.TopIndex = _log.Items.Count - 1;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopBlocking("window closed");
        base.OnFormClosed(e);
    }
}

// Same shape as CatFoil's KeyboardHook, for WH_MOUSE_LL.
internal sealed class MouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    private readonly Func<uint, MSLLHOOKSTRUCT, bool> _shouldEat;
    private readonly HookProc _proc;   // field keeps the delegate alive for native code
    private IntPtr _hook;

    public MouseHook(Func<uint, MSLLHOOKSTRUCT, bool> shouldEat)
    {
        _shouldEat = shouldEat;
        _proc = Callback;
    }

    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = SetWindowsHookExW(WH_MOUSE_LL, _proc, GetModuleHandleW(null), 0);
        return _hook != IntPtr.Zero;
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (_shouldEat((uint)wParam, data))
                return (IntPtr)1;
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
