namespace WorkFileExplorer.App.Models;

public sealed class QuickAccessItem
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool IsPinned { get; init; }
}