using System.Windows;

namespace WorkFileExplorer.App.Dialogs;

public partial class StyledDialogWindow : Window
{
    public StyledDialogWindow(string title, string message, bool confirmDialog)
    {
        InitializeComponent();
        Title = title;
        MessageTextBlock.Text = message;

        if (!confirmDialog)
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = "확인(O)";
        }
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
        DialogResult = true;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

