using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace WorkFileExplorer.App.Dialogs;

/// <summary>
/// Non-modal progress window for long file operations (copy/move/delete).
/// Appears only when the operation outlives a short grace period, reports
/// item-level progress from background threads, and exposes a cancel token.
/// </summary>
public partial class TransferProgressWindow : Window, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _operationName;
    private readonly int _totalItems;
    private bool _completed;
    private bool _workStarted;

    public CancellationToken Token => _cts.Token;

    private TransferProgressWindow(string operationName, int totalItems)
    {
        InitializeComponent();
        _operationName = operationName;
        _totalItems = Math.Max(1, totalItems);
        Title = $"{operationName} 진행";
        OperationText.Text = $"{operationName} 준비 중...";
        ProgressBarControl.Maximum = _totalItems;
    }

    /// <summary>
    /// Creates the window on the UI thread. It stays hidden until
    /// <see cref="NotifyWorkStarted"/> is called, so it never pops up while a
    /// conflict prompt is still waiting for the user.
    /// </summary>
    public static TransferProgressWindow Start(string operationName, int totalItems)
    {
        var window = new TransferProgressWindow(operationName, totalItems);
        var owner = Application.Current?.MainWindow;
        if (owner is not null && owner.IsLoaded)
        {
            window.Owner = owner;
        }

        return window;
    }

    /// <summary>Call when actual file work begins (after any user prompts); idempotent, safe from any thread.</summary>
    public void NotifyWorkStarted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_workStarted || _completed)
            {
                return;
            }

            _workStarted = true;
            Show();
        });
    }

    /// <summary>Item-level progress; safe to call from any thread.</summary>
    public void ReportItem(int completedItems, string? currentPath)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ProgressBarControl.Value = Math.Min(completedItems, _totalItems);
            OperationText.Text = $"{_operationName} 중... ({Math.Min(completedItems + 1, _totalItems)}/{_totalItems})";
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                CurrentFileText.Text = currentPath;
                CurrentFileText.ToolTip = currentPath;
            }
        });
    }

    /// <summary>Updates only the current-file line (e.g. while copying a directory's contents); safe to call from any thread.</summary>
    public void ReportCurrentFile(string path)
    {
        Dispatcher.BeginInvoke(() =>
        {
            CurrentFileText.Text = path;
            CurrentFileText.ToolTip = path;
        });
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        RequestCancel();
    }

    private void RequestCancel()
    {
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        CancelButton.Content = "취소 중...";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The X button acts as a cancel request; the window closes when the operation ends.
        if (!_completed)
        {
            e.Cancel = true;
            RequestCancel();
            return;
        }

        base.OnClosing(e);
    }

    public void Dispose()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _completed = true;
            if (IsVisible)
            {
                Close();
            }

            _cts.Dispose();
        });
    }
}
