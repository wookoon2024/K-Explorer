using System.ComponentModel;
using System.IO;
using System.Windows;

namespace WorkFileExplorer.App.Dialogs;

public partial class NewFolderDialog : Window, INotifyPropertyChanged
{
    private string _folderName = "New Folder";

    public NewFolderDialog(string initialName)
    {
        InitializeComponent();
        DataContext = this;
        FolderName = string.IsNullOrWhiteSpace(initialName) ? "New Folder" : initialName.Trim();

        Loaded += (_, _) =>
        {
            FolderNameTextBox.Focus();
            FolderNameTextBox.SelectAll();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FolderName
    {
        get => _folderName;
        set
        {
            if (string.Equals(_folderName, value, StringComparison.Ordinal))
            {
                return;
            }

            _folderName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FolderName)));
        }
    }

    public static string? ShowDialog(Window owner, string initialName = "New Folder")
    {
        var dialog = new NewFolderDialog(initialName)
        {
            Owner = owner
        };

        var accepted = dialog.ShowDialog() == true;
        return accepted ? dialog.FolderName.Trim() : null;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var name = (FolderName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StyledDialogWindow.ShowInfo(this, "알림", "폴더 이름을 입력해 주세요.");
            return;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StyledDialogWindow.ShowInfo(this, "알림", "폴더 이름에 사용할 수 없는 문자가 있습니다.");
            return;
        }

        FolderName = name;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
