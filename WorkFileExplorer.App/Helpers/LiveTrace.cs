using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorkFileExplorer.App.Helpers;

public static class LiveTrace
{
    private static readonly object Gate = new();
    private static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "live_trace.log");
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("KEXPLORER_LIVETRACE"), "1", StringComparison.OrdinalIgnoreCase);
    private static bool _initialized;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);

    private const int AttachParentProcess = -1;

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

#if DEBUG
            // Only attach/create console in Debug so Release users do not see a log window.
            if (!AttachConsole(AttachParentProcess))
            {
                AllocConsole();
            }

            SetConsoleOutputCP(65001);
#endif
            _initialized = true;
            try
            {
                File.WriteAllText(LogFile, string.Empty);
            }
            catch
            {
            }
            Write("LiveTrace initialized");
        }
    }

    public static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [T{Environment.CurrentManagedThreadId}] {message}";
#if DEBUG
            try
            {
                Console.WriteLine(line);
            }
            catch
            {
            }
#endif

            try
            {
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch
            {
            }
        }
    }

    public static void WriteProcessSnapshot(string tag)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            var workingSetMb = process.WorkingSet64 / (1024d * 1024d);
            var privateMb = process.PrivateMemorySize64 / (1024d * 1024d);
            var gcMb = GC.GetTotalMemory(forceFullCollection: false) / (1024d * 1024d);
            var cpuMs = process.TotalProcessorTime.TotalMilliseconds;
            Write($"{tag} perf cpuMs={cpuMs:N0} wsMb={workingSetMb:N1} privateMb={privateMb:N1} handles={process.HandleCount} threads={process.Threads.Count} gcMb={gcMb:N1}");
        }
        catch (Exception ex)
        {
            Write($"{tag} perf snapshot failed: {ex.GetType().Name}");
        }
    }
}
