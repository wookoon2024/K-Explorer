using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;

namespace WorkFileExplorer.App.Helpers;

/// <summary>
/// Temporary diagnostics: logs dispatcher operations that block the UI thread
/// longer than a threshold, including the target method when resolvable.
/// </summary>
public static class DispatcherDiag
{
    private const int SlowThresholdMs = 30;

    private static readonly Dictionary<DispatcherOperation, long> Pending = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly FieldInfo? MethodField =
        typeof(DispatcherOperation).GetField("_method", BindingFlags.NonPublic | BindingFlags.Instance);

    public static void Init(Dispatcher dispatcher)
    {
        dispatcher.Hooks.OperationStarted += (_, e) =>
        {
            lock (Pending)
            {
                Pending[e.Operation] = Clock.ElapsedMilliseconds;
            }
        };

        dispatcher.Hooks.OperationCompleted += (_, e) => Complete(e.Operation);
        dispatcher.Hooks.OperationAborted += (_, e) =>
        {
            lock (Pending)
            {
                Pending.Remove(e.Operation);
            }
        };
    }

    private static void Complete(DispatcherOperation operation)
    {
        long startedAt;
        lock (Pending)
        {
            if (!Pending.TryGetValue(operation, out startedAt))
            {
                return;
            }

            Pending.Remove(operation);
        }

        var elapsed = Clock.ElapsedMilliseconds - startedAt;
        if (elapsed < SlowThresholdMs)
        {
            return;
        }

        var name = "(unknown)";
        try
        {
            if (MethodField?.GetValue(operation) is Delegate target)
            {
                name = $"{target.Method.DeclaringType?.FullName}.{target.Method.Name}";
            }
        }
        catch
        {
        }

        LiveTrace.Write($"SlowDispatcherOp {elapsed}ms priority={operation.Priority} method={name}");
    }
}
