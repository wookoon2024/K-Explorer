namespace WorkFileExplorer.App.Models;

public sealed class TrackedFileRecord
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime LastAccessUtc { get; set; }
    public int AccessCount { get; set; }
    public bool IsPinned { get; set; }
}