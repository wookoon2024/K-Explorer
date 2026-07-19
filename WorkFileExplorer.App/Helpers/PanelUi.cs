using System.Windows;

namespace WorkFileExplorer.App.Helpers;

/// <summary>
/// Attached property that marks a panel's item controls (DataGrid/ListBox) as
/// belonging to the active panel. It inherits down the visual tree, so row and
/// cell styles can dim the selection highlight in inactive panels.
/// </summary>
public static class PanelUi
{
    public static readonly DependencyProperty IsActivePanelProperty =
        DependencyProperty.RegisterAttached(
            "IsActivePanel",
            typeof(bool),
            typeof(PanelUi),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsActivePanel(DependencyObject obj) => (bool)obj.GetValue(IsActivePanelProperty);

    public static void SetIsActivePanel(DependencyObject obj, bool value) => obj.SetValue(IsActivePanelProperty, value);
}
