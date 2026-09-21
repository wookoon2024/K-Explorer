namespace WorkFileExplorer.App.ViewModels;

/// <summary>
/// A combo box entry that shows a Korean label while the stored value stays in its
/// own form, so settings files and comparisons keep working unchanged.
/// </summary>
public sealed record DisplayOption(string Value, string Label)
{
    public override string ToString() => Label;
}
