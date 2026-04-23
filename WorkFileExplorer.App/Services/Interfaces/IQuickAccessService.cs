using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.Services.Interfaces;

public interface IQuickAccessService
{
    Task<IReadOnlyList<QuickAccessItem>> GetItemsAsync(AppSettings settings, UsageSnapshot snapshot, CancellationToken cancellationToken = default);
}