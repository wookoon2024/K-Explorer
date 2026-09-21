using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.Dialogs;

public partial class ShortcutEditDialog : Window
{
    private sealed record KeyOption(Key Key, string Display)
    {
        // Keeps screen readers and UI automation on the friendly label.
        public override string ToString() => Display;
    }

    public ShortcutEditDialog(string commandName, ShortcutGesture current)
    {
        InitializeComponent();

        Title = $"단축키 변경 - {commandName}";
        CommandText.Text = commandName;

        var options = new List<KeyOption> { new(Key.None, ShortcutGesture.FormatKeyName(Key.None)) };
        foreach (var key in ShortcutCatalog.SelectableKeys)
        {
            options.Add(new KeyOption(key, ShortcutGesture.FormatKeyName(key)));
        }

        // Keep an unknown-but-assigned key selectable instead of silently dropping it.
        if (current.IsAssigned && options.All(option => option.Key != current.Key))
        {
            options.Add(new KeyOption(current.Key, ShortcutGesture.FormatKeyName(current.Key)));
        }

        ComboKey.ItemsSource = options;
        ComboKey.SelectedItem = options.FirstOrDefault(option => option.Key == current.Key) ?? options[0];

        CheckCtrl.IsChecked = (current.Modifiers & ModifierKeys.Control) != 0;
        CheckShift.IsChecked = (current.Modifiers & ModifierKeys.Shift) != 0;
        CheckAlt.IsChecked = (current.Modifiers & ModifierKeys.Alt) != 0;
        CheckWin.IsChecked = (current.Modifiers & ModifierKeys.Windows) != 0;

        UpdatePreview();

        Loaded += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () => ComboKey.Focus());
    }

    public ShortcutGesture Result { get; private set; } = ShortcutGesture.Unassigned;

    public static ShortcutGesture? Show(Window owner, string commandName, ShortcutGesture current)
    {
        var dialog = new ShortcutEditDialog(commandName, current) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private ShortcutGesture CurrentSelection()
    {
        var key = (ComboKey.SelectedItem as KeyOption)?.Key ?? Key.None;
        if (key == Key.None)
        {
            return ShortcutGesture.Unassigned;
        }

        var modifiers = ModifierKeys.None;
        if (CheckCtrl.IsChecked == true)
        {
            modifiers |= ModifierKeys.Control;
        }

        if (CheckShift.IsChecked == true)
        {
            modifiers |= ModifierKeys.Shift;
        }

        if (CheckAlt.IsChecked == true)
        {
            modifiers |= ModifierKeys.Alt;
        }

        if (CheckWin.IsChecked == true)
        {
            modifiers |= ModifierKeys.Windows;
        }

        return new ShortcutGesture(key, modifiers);
    }

    private void UpdatePreview()
    {
        var gesture = CurrentSelection();
        PreviewText.Text = gesture.IsAssigned ? gesture.ToDisplayText() : ShortcutGesture.FormatKeyName(Key.None);
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void OnKeySelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Result = ShortcutGesture.Unassigned;
        DialogResult = true;
        Close();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var gesture = CurrentSelection();
        if (!gesture.IsAssigned)
        {
            StyledDialogWindow.ShowInfo(this, "알림", "키를 선택해 주세요. 지정을 비우려면 '지정 해제'를 누르세요.");
            return;
        }

        Result = gesture;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
