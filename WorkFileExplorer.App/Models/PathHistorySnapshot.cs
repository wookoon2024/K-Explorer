namespace WorkFileExplorer.App.Models;

public sealed class PathHistorySnapshot
{
    public IReadOnlyList<string> LeftPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RightPaths { get; init; } = Array.Empty<string>();
}
