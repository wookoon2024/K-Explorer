using System.Windows;

namespace WorkFileExplorer.App.Helpers;

/// <summary>
/// Lets non-visual objects (DataGridColumn) bind to the window's DataContext.
/// Columns are not part of the visual tree, so RelativeSource bindings cannot reach it.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
