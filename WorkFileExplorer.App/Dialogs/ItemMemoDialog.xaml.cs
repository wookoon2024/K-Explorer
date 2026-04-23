using System.ComponentModel;
using System.Windows;

namespace WorkFileExplorer.App.Dialogs;

public sealed class ItemMemoDialogResult
{
    public ItemMemoDialogResult(string memoText, bool deleteRequested)
    {
        MemoText = memoText;
        DeleteRequested = deleteRequested;
    }

    public string MemoText { get; }

    public bool DeleteRequested { get; }
}

public partial class ItemMemoDialog : Window, INotifyPropertyChanged
{
    private string _memoText = string.Empty;
    private string _targetPath = string.Empty;
    private bool _deleteRequested;

    public ItemMemoDialog(string targetPath, string initialMemo)
    {
        InitializeComponent();
        DataContext = this;
        TargetPath = targetPath ?? string.Empty;
        MemoText = initialMemo ?? string.Empty;

        Loaded += (_, _) =>
        {
            MemoTextBox.Focus();
            MemoTextBox.CaretIndex = MemoTextBox.Text.Length;
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (string.Equals(_targetPath, value, StringComparison.Ordinal))
            {
                return;
            }

            _targetPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetPath)));
        }
    }

    public string MemoText
    {
        get => _memoText;
        set
        {
            if (string.Equals(_memoText, value, StringComparison.Ordinal))
            {
                return;
            }

            _memoText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MemoText)));
        }
    }

    public static ItemMemoDialogResult? ShowDialog(Window owner, string targetName, string targetPath, string initialMemo)
    {
        var dialog = new ItemMemoDialog(targetPath, initialMemo)
        {
            Owner = owner,
            Title = $"메모 편집 - {targetName}"
        };

        var accepted = dialog.ShowDialog() == true;
        if (!accepted)
        {
            return null;
        }

        return new ItemMemoDialogResult(dialog.MemoText.Trim(), dialog._deleteRequested);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _deleteRequested = false;
        DialogResult = true;
        Close();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        _deleteRequested = true;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
