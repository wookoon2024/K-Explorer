using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.Services.Interfaces;

public interface IPathHistoryStoreService
{
    Task<PathHistorySnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<string> leftPaths, IReadOnlyList<string> rightPaths, CancellationToken cancellationToken = default);
}
