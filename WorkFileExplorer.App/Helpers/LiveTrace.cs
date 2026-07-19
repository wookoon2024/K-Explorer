using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorkFileExplorer.App.Helpers;

public static class LiveTrace
{
    private static readonly object Gate = new();
    private static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "live_trace.log");
    private static readonly Dictionary<string, DateTime> LastSnapshotByTag = new(StringComparer.Ordinal);
    private static readonly TimeSpan SnapshotMinInterval = TimeSpan.FromMilliseconds(500);
    private static readonly ConcurrentQueue<string> PendingLines = new();
    private static readonly AutoResetEvent WriteSignal = new(false);
#if DEBUG
    private static readonly bool Enabled = true;
#else
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("KEXPLORER_LIVETRACE"), "1", StringComparison.OrdinalIgnoreCase);
#endif
    private static bool _initialized;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    private const uint GR_GDIOBJECTS = 0;
    private const uint GR_USEROBJECTS = 1;

    public static void Init()
    {
        if (!Enabled)
        {
            _initialized = true;
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            try
            {
                File.WriteAllText(LogFile, string.Empty);
            }
            catch
            {
            }

            // Writing synchronously (file append + console) on the UI thread makes
            // hot paths (typing, selection changes) visibly lag; a background
            // writer drains the queue instead.
            var writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "LiveTraceWriter"
            };
            writerThread.Start();

            Write("LiveTrace initialized");
        }
    }

    public static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        PendingLines.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] [T{Environment.CurrentManagedThreadId}] {message}");
        WriteSignal.Set();
    }

    private static void WriterLoop()
    {
        StreamWriter? writer = null;
        try
        {
            writer = new StreamWriter(new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        }
        catch
        {
        }

        while (true)
        {
            WriteSignal.WaitOne(500);
            var wroteAny = false;
            while (PendingLines.TryDequeue(out var line))
            {
                wroteAny = true;
                try
                {
                    writer?.WriteLine(line);
                }
                catch
                {
                }
            }

            if (wroteAny)
            {
                try
                {
                    writer?.Flush();
                }
                catch
                {
                }
            }
        }
    }

    public static void WriteProcessSnapshot(string tag)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (LastSnapshotByTag.TryGetValue(tag, out var lastAt) &&
                now - lastAt < SnapshotMinInterval)
            {
                return;
            }

            LastSnapshotByTag[tag] = now;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            var workingSetMb = process.WorkingSet64 / (1024d * 1024d);
            var privateMb = process.PrivateMemorySize64 / (1024d * 1024d);
            var gcMb = GC.GetTotalMemory(forceFullCollection: false) / (1024d * 1024d);
            var cpuMs = process.TotalProcessorTime.TotalMilliseconds;
            uint gdi = 0, user = 0;
            try
            {
                var h = process.Handle;
                gdi = GetGuiResources(h, GR_GDIOBJECTS);
                user = GetGuiResources(h, GR_USEROBJECTS);
            }
            catch
            {
            }
            var uiaFlags = "n/a";
            try
            {
                uiaFlags =
                    $"struct={System.Windows.Automation.Peers.AutomationPeer.ListenerExists(System.Windows.Automation.Peers.AutomationEvents.StructureChanged)}" +
                    $",prop={System.Windows.Automation.Peers.AutomationPeer.ListenerExists(System.Windows.Automation.Peers.AutomationEvents.PropertyChanged)}" +
                    $",focus={System.Windows.Automation.Peers.AutomationPeer.ListenerExists(System.Windows.Automation.Peers.AutomationEvents.AutomationFocusChanged)}" +
                    $",selAdd={System.Windows.Automation.Peers.AutomationPeer.ListenerExists(System.Windows.Automation.Peers.AutomationEvents.SelectionItemPatternOnElementSelected)}";
            }
            catch
            {
            }
            Write($"{tag} perf cpuMs={cpuMs:N0} wsMb={workingSetMb:N1} privateMb={privateMb:N1} handles={process.HandleCount} gdi={gdi} user={user} threads={process.Threads.Count} gcMb={gcMb:N1} uia[{uiaFlags}]");
        }
        catch (Exception ex)
        {
            Write($"{tag} perf snapshot failed: {ex.GetType().Name}");
        }
    }
}
