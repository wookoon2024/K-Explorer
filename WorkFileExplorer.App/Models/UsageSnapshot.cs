namespace WorkFileExplorer.App.Models;

public sealed class UsageSnapshot
{
    public IReadOnlyList<TrackedFolderRecord> RecentFolders { get; init; } = Array.Empty<TrackedFolderRecord>();
    public IReadOnlyList<TrackedFolderRecord> FrequentFolders { get; init; } = Array.Empty<TrackedFolderRecord>();
    public IReadOnlyList<TrackedFileRecord> RecentFiles { get; init; } = Array.Empty<TrackedFileRecord>();
    public IReadOnlyList<TrackedFileRecord> FrequentFiles { get; init; } = Array.Empty<TrackedFileRecord>();
}