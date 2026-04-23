namespace WorkFileExplorer.App.Models;

public sealed class UsageData
{
    public List<TrackedFolderRecord> FolderRecords { get; set; } = new();
    public List<TrackedFileRecord> FileRecords { get; set; } = new();
}