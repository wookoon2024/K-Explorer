using System.Windows;

namespace WorkFileExplorer.App.Dialogs;

public partial class StyledDialogWindow : Window
{
    public enum ConflictChoice
    {
        Cancel = 0,
        Overwrite = 1,
        RenameNew = 2,
        OverwriteAll = 3,
        RenameNewAll = 4
    }

    private ConflictChoice _conflictChoice = ConflictChoice.Cancel;

    public StyledDialogWindow(string title, string message, bool confirmDialog)
    {
        InitializeComponent();
        Title = title;
        MessageTextBlock.Text = message;

        if (!confirmDialog)
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
            TertiaryButton.Visibility = Visibility.Collapsed;
            QuaternaryButton.Visibility = Visibility.Collapsed;
            QuinaryButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = "확인(O)";
        }
    }

    public static ConflictChoice ShowConflictChoice(Window owner, string title, string message, bool showApplyAllOptions = false)
    {
        var dialog = new StyledDialogWindow(title, message, confirmDialog: true)
        {
            Owner = owner
        };

        dialog.PrimaryButton.Content = "덮어쓰기(O)";
        dialog.SecondaryButton.Content = "취소(N)";
        dialog.SecondaryButton.IsCancel = true;
        dialog.TertiaryButton.Content = "이름변경 복사(R)";
        dialog.TertiaryButton.Visibility = Visibility.Visible;
        dialog.QuaternaryButton.Content = "모두 덮어쓰기(A)";
        dialog.QuinaryButton.Content = "모두 이름변경복사(L)";
        dialog.QuaternaryButton.Visibility = showApplyAllOptions ? Visibility.Visible : Visibility.Collapsed;
        dialog.QuinaryButton.Visibility = showApplyAllOptions ? Visibility.Visible : Visibility.Collapsed;

        var result = dialog.ShowDialog();
        if (result == true)
        {
            return dialog._conflictChoice;
        }

        return ConflictChoice.Cancel;
    }

    public static bool ShowConfirm(Window owner, string title, string message)
    {
        var dialog = new StyledDialogWindow(title, message, confirmDialog: true)
        {
            Owner = owner
        };

        var result = dialog.ShowDialog();
        return result == true;
    }

    public static void ShowInfo(Window owner, string title, string message)
    {
        var dialog = new StyledDialogWindow(title, message, confirmDialog: false)
        {
            Owner = owner
        };

        dialog.ShowDialog();
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        _conflictChoice = ConflictChoice.Overwrite;
        DialogResult = true;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        _conflictChoice = ConflictChoice.Cancel;
        DialogResult = false;
        Close();
    }

    private void OnTertiaryClick(object sender, RoutedEventArgs e)
    {
        _conflictChoice = ConflictChoice.RenameNew;
        DialogResult = true;
        Close();
    }

    private void OnQuaternaryClick(object sender, RoutedEventArgs e)
    {
        _conflictChoice = ConflictChoice.OverwriteAll;
        DialogResult = true;
        Close();
    }

    private void OnQuinaryClick(object sender, RoutedEventArgs e)
    {
        _conflictChoice = ConflictChoice.RenameNewAll;
        DialogResult = true;
        Close();
    }
}
