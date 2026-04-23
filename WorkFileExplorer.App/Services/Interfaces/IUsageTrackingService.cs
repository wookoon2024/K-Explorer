using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.Services.Interfaces;

public interface IUsageTrackingService
{
    void RecordFolderAccess(string path, bool pinned);
    void RecordFileOpen(string path, bool pinned);
    Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task PersistAsync(CancellationToken cancellationToken = default);
}