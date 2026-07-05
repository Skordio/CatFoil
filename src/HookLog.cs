using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace CatFoil;

/// <summary>
/// Opt-in diagnostic log for the keyboard hook. Enabled by setting the
/// <c>CATFOIL_HOOK_LOG</c> environment variable: "1" (or "true") logs to
/// <c>%APPDATA%\CatFoil\hook-diagnostic.log</c>, any other value is treated as a
/// full file path. Used to confirm empirically which key events actually reach
/// the hook while locked and whether they were swallowed — e.g. to see whether
/// Win+G is even visible to the hook or is dispatched by Windows off the hook path.
///
/// Zero cost when disabled. When enabled, the hook callback only enqueues a string
/// (no file I/O on the callback thread), so it can't overrun LowLevelHooksTimeout
/// and get the hook silently removed; a background timer flushes to disk.
/// </summary>
internal static class HookLog
{
    public static readonly bool Enabled;
    private static readonly string _path = "";
    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly System.Threading.Timer? _flusher;

    static HookLog()
    {
        string? v = Environment.GetEnvironmentVariable("CATFOIL_HOOK_LOG");
        Enabled = !string.IsNullOrWhiteSpace(v);
        if (!Enabled) return;

        _path = v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Settings.Directory, "hook-diagnostic.log")
            : v!;
        try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); } catch { /* best effort */ }

        Record($"=== CatFoil hook diagnostic started {DateTime.Now:yyyy-MM-dd HH:mm:ss} (log: {_path}) ===");
        _flusher = new System.Threading.Timer(_ => Flush(), null, 1000, 1000);
    }

    /// <summary>Enqueue a timestamped line. Cheap; safe to call from the hook.</summary>
    public static void Record(string line)
    {
        if (!Enabled) return;
        _queue.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {line}");
    }

    private static void Flush()
    {
        if (_queue.IsEmpty) return;
        var sb = new StringBuilder();
        while (_queue.TryDequeue(out string? l)) sb.AppendLine(l);
        try { File.AppendAllText(_path, sb.ToString()); } catch { /* best effort */ }
    }
}
