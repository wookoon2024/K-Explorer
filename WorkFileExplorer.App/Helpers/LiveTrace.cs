using System.Runtime.InteropServices;

namespace WorkFileExplorer.App.Helpers;

public static class LiveTrace
{
    private static readonly object Gate = new();
    private static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "live_trace.log");
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
}
