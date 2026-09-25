using System.Windows;

namespace WorkFileExplorer.App.Helpers;

public static class PanelUi
{
    public static readonly DependencyProperty IsActivePanelProperty =
        DependencyProperty.RegisterAttached(
            "IsActivePanel",
            typeof(bool),
            typeof(PanelUi),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty PanelEntryProperty =
        DependencyProperty.RegisterAttached(
            "PanelEntry",
            typeof(object),
            typeof(PanelUi),
            new FrameworkPropertyMetadata(null));

    public static bool GetIsActivePanel(DependencyObject obj) => (bool)obj.GetValue(IsActivePanelProperty);

    public static void SetIsActivePanel(DependencyObject obj, bool value) => obj.SetValue(IsActivePanelProperty, value);

    public static object? GetPanelEntry(DependencyObject obj) => obj.GetValue(PanelEntryProperty);

    public static void SetPanelEntry(DependencyObject obj, object? value) => obj.SetValue(PanelEntryProperty, value);
}
